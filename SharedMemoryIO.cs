using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace GKMD.Internal;

/// <summary>
/// Per-controller input/output channel between <see cref="GKController"/>
/// and the in-process USB/IP device emulator
/// (<c>UsbipEmulatedDevice</c>).
///
/// <para>Both ends live in the same process now that every persona is a
/// USB/IP device: the controller writes the newest input frame, the
/// emulated device's input pump reads it and turns it into the URB the
/// host is waiting for, and the device publishes captured
/// rumble/haptics/FFB/LED packets back for the controller's output poll
/// thread. The channel is therefore a plain block of zeroed native memory
/// plus a pair of managed auto-reset events, which keeps it portable
/// (Windows, Linux, any architecture) and removes the named-section and
/// named-event Win32 plumbing the old kernel driver needed.</para>
///
/// <para>The memory layout is unchanged from that driver-era design, so
/// the writers and readers below stay the same seqlock code they always
/// were: single writer, many readers, readers retry on a sequence
/// mismatch. The static state is per process and keyed by controller
/// index, matching the SDK's controller-index pool.</para>
/// </summary>
internal static class SharedMemoryIO
{
    // ── Layout constants ──────────────────────────────────────────────────
    //
    // GKMD_SHARED_INPUT (362 bytes, was 278 pre-v1.3.5):
    //   ULONG  SeqNo                offset 0
    //   ULONG  DataSize             offset 4
    //   UCHAR  Data[256]            offset 8     ← legacy report data, RID stripped
    //   UCHAR  GipData[14]          offset 264
    //   ULONG  ExtendedReportSize   offset 278   ← v1.3.5: 0 = legacy, >0 = use ExtendedReportData
    //   UCHAR  ExtendedReportData[80] offset 282 ← v1.3.5: full RID-included report (Sony BT 0x31 / 0x11)
    //
    // A frame carries EITHER legacy OR extended per frame (mode-switch),
    // not both. The writer clears ExtendedReportSize on legacy frames so a
    // previously-armed controller's stale extended bytes do not leak
    // through.
    //
    // GKMD_SHARED_OUTPUT — RING BUFFER as of v1.1.40.
    //   ULONG  Head                        offset 0   (monotonic; total writes)
    //   ULONG  _Reserved                   offset 4
    //   GKMD_OUTPUT_SLOT Slots[N]    offset 8
    //
    // Each slot (264 bytes):
    //   ULONG  SeqNo                  slot+0    (== Head value at the time of write)
    //   UCHAR  Source                 slot+4
    //   UCHAR  ReportId               slot+5
    //   USHORT DataSize               slot+6
    //   UCHAR  Data[256]              slot+8
    //
    // Pre-1.1.40 was a single slot, latest-write-wins. That coalesced
    // pid.dll's PID FFB write bursts (Set Effect → Set Constant Force →
    // Effect Operation Start, all within 1-3 ms) vs the SDK's 8 ms
    // poll interval. Middle packet (the magnitude) dropped, which is
    // why FFB forces never reached physical devices despite CreateEffect
    // succeeding. v1.1.40 ring with N=64 slots gives ~512 ms of reader
    // lag tolerance before oldest packets get overwritten — comfortable
    // headroom for a 125 Hz consumer vs ~3 ms write bursts.
    //
    // Data[] widened from 64→256 bytes 2026-04-23: DualSense BT report 0x31
    // is 78 bytes; Switch Pro standard input report can run to ~64.

    public const int DATA_OFFSET                = 8;
    public const int DATA_CAPACITY              = 256;
    public const int GIP_DATA_OFFSET            = DATA_OFFSET + DATA_CAPACITY;   // 264
    public const int GIP_DATA_LENGTH            = 14;
    // v1.3.5 — vendor-blob extended report (mode-switch path).
    public const int EXTENDED_SIZE_OFFSET       = GIP_DATA_OFFSET + GIP_DATA_LENGTH; // 278
    public const int EXTENDED_DATA_OFFSET       = EXTENDED_SIZE_OFFSET + 4;          // 282
    public const int EXTENDED_DATA_CAPACITY     = 80;
    public const int SHARED_INPUT_SIZE          = EXTENDED_DATA_OFFSET + EXTENDED_DATA_CAPACITY; // 362

    // Output ring layout:
    public const int OUTPUT_RING_SLOTS  = 64;
    public const int OUTPUT_SLOT_SIZE   = 4 + 1 + 1 + 2 + 256;     // SeqNo + Source + RID + DataSize + Data[256]
    public const int OUTPUT_HEADER_SIZE = 4 + 4;                   // Head + _Reserved
    public const int SHARED_OUTPUT_SIZE = OUTPUT_HEADER_SIZE + OUTPUT_RING_SLOTS * OUTPUT_SLOT_SIZE;
    public const int OUTPUT_SLOT_OFFSET_SEQNO     = 0;
    public const int OUTPUT_SLOT_OFFSET_SOURCE    = 4;
    public const int OUTPUT_SLOT_OFFSET_REPORT_ID = 5;
    public const int OUTPUT_SLOT_OFFSET_SIZE      = 6;
    public const int OUTPUT_SLOT_OFFSET_DATA      = 8;

    private static readonly Dictionary<int, IntPtr> s_inputViews  = new();
    private static readonly Dictionary<int, AutoResetEvent> s_inputEvents = new();
    private static readonly Dictionary<int, IntPtr> s_outputViews   = new();
    private static readonly Dictionary<int, AutoResetEvent> s_outputEvents  = new();

    /// <summary>Live output-poll threads, keyed by controller index (issue
    /// #45).
    ///
    /// <para>A poll thread dereferences its output view on every iteration
    /// and is stopped only by its owning <c>GKController.Dispose</c>.
    /// Unmapping that view while the thread runs is an access violation on a
    /// background thread, which takes the process down from a call the
    /// consumer never associated with a controller: the device sweep ends in
    /// <see cref="Cleanup"/>, and a consumer that swept while still holding a
    /// controller crashed there reproducibly.</para>
    ///
    /// <para>Registering the threads here puts the fix at the point of
    /// danger rather than at one call site, so every unmap path is covered
    /// and not just the sweep that happened to expose it.</para></summary>
    private static readonly Dictionary<int, (CancellationTokenSource Cts, Thread Thread)> s_outputPumps = new();

    /// <summary>Records a controller's output-poll thread so any later unmap
    /// can stop it first. Called once the thread is running.</summary>
    public static void RegisterOutputPump(int controllerIndex, CancellationTokenSource cts, Thread thread)
    {
        lock (s_outputPumps) s_outputPumps[controllerIndex] = (cts, thread);
    }

    /// <summary>Drops a controller's poll-thread registration without
    /// stopping it. For <c>GKController.Dispose</c>, which has already
    /// cancelled and joined its own thread.</summary>
    public static void UnregisterOutputPump(int controllerIndex)
    {
        lock (s_outputPumps) s_outputPumps.Remove(controllerIndex);
    }

    /// <summary>Cancels and joins the named controller's poll thread, if one
    /// is registered. Idempotent, and safe to call on an already-stopped
    /// thread.</summary>
    private static void StopOutputPump(int controllerIndex)
    {
        (CancellationTokenSource Cts, Thread Thread) pump;
        lock (s_outputPumps)
        {
            if (!s_outputPumps.TryGetValue(controllerIndex, out pump)) return;
            s_outputPumps.Remove(controllerIndex);
        }
        JoinPump(pump);
    }

    /// <summary>Cancels and joins every registered poll thread.</summary>
    private static void StopAllOutputPumps()
    {
        (CancellationTokenSource Cts, Thread Thread)[] pumps;
        lock (s_outputPumps)
        {
            pumps = new (CancellationTokenSource, Thread)[s_outputPumps.Count];
            s_outputPumps.Values.CopyTo(pumps, 0);
            s_outputPumps.Clear();
        }
        foreach (var pump in pumps) JoinPump(pump);
    }

    /// <summary>Cancel then join, outside the registry lock.
    ///
    /// <para>The join must not happen under <c>s_outputPumps</c>: the poll
    /// loop calls back into this class, so holding a lock across the join
    /// invites a deadlock with a thread that is trying to acquire it. The
    /// snapshot-then-join shape above is what keeps that impossible.</para>
    ///
    /// <para>Joining the current thread would deadlock outright, which is
    /// reachable if a consumer's OutputReceived handler triggers a sweep, so
    /// that case cancels and returns rather than waiting on itself.</para></summary>
    private static void JoinPump((CancellationTokenSource Cts, Thread Thread) pump)
    {
        try { pump.Cts?.Cancel(); } catch { /* already disposed */ }
        if (pump.Thread == null || !pump.Thread.IsAlive) return;
        if (ReferenceEquals(pump.Thread, Thread.CurrentThread)) return;
        try { pump.Thread.Join(TimeoutScale.Apply(500)); } catch { }
    }

    /// <summary>Returns the view pointer for the controller's INPUT block,
    /// creating it on first call. Thread-safe via per-call lock — callers can
    /// issue concurrent EnsureInputMapping requests for distinct
    /// controllerIndex values without races. Idempotent for the same index.</summary>
    public static unsafe IntPtr EnsureInputMapping(int controllerIndex)
    {
        lock (s_inputViews)
        {
            if (s_inputViews.TryGetValue(controllerIndex, out IntPtr existing))
                return existing;

            // AllocZeroed: page-backed zeroed memory, so SeqNo starts at 0
            // and the GIP slice stays zeros for non-Xbox profiles.
            IntPtr view = (IntPtr)NativeMemory.AllocZeroed(SHARED_INPUT_SIZE);

            // The input pump waits on this with a timeout; the controller
            // signals it per frame so a parked read wakes immediately.
            // Auto-reset so a single wait consumes the signal.
            s_inputViews[controllerIndex] = view;
            s_inputEvents[controllerIndex] = new AutoResetEvent(false);
            return view;
        }
    }

    /// <summary>Returns the view pointer for the controller's OUTPUT block,
    /// creating it on first call. The device emulator attaches to this block
    /// read-write to publish captured rumble/haptics/FFB/LED.</summary>
    public static unsafe IntPtr EnsureOutputMapping(int controllerIndex)
    {
        lock (s_outputViews)
        {
            if (s_outputViews.TryGetValue(controllerIndex, out IntPtr existing))
                return existing;

            IntPtr view = (IntPtr)NativeMemory.AllocZeroed(SHARED_OUTPUT_SIZE);
            s_outputViews[controllerIndex] = view;
            return view;
        }
    }

    /// <summary>Create (or return) the output-ring doorbell
    /// <c>GKMDOutputEvent&lt;N&gt;</c> (issue #39). The USB/IP backend has no
    /// external driver: the in-process device emulator creates it here
    /// BEFORE the GKController's OutputPollLoop asks for it, so the reader
    /// comes up in event mode. The event object is shared by the device
    /// (writer) and the controller (sole waiter) and is closed by
    /// DestroyController.</summary>
    public static AutoResetEvent EnsureOutputEvent(int controllerIndex)
    {
        lock (s_outputViews)
        {
            if (s_outputEvents.TryGetValue(controllerIndex, out AutoResetEvent? existing))
                return existing;
            var ev = new AutoResetEvent(false);
            s_outputEvents[controllerIndex] = ev;
            return ev;
        }
    }

    /// <summary>The output-ring doorbell, or null if no mapping has been
    /// created for that index yet. Used by <see cref="GKController"/> to
    /// block on published packets instead of polling at 8 ms.</summary>
    public static AutoResetEvent? TryOpenOutputEvent(int controllerIndex)
    {
        lock (s_outputViews)
            return s_outputEvents.TryGetValue(controllerIndex, out AutoResetEvent? ev) ? ev : null;
    }

    /// <summary>Companion input doorbell handle for
    /// <c>GKMDCompanionInputEvent&lt;N&gt;</c>. The XUSB companion that
    /// once consumed it is gone (every persona rides USB/IP), so this is
    /// always null and kept only for the legacy non-USB/IP call site.</summary>
    public static AutoResetEvent? GetCompanionInputEvent(int controllerIndex) => null;

    /// <summary>Returns the signaling event for a controller's input block,
    /// or null if no mapping has been created for that index yet. Used by
    /// <see cref="GKController"/> to cache the event alongside the view
    /// pointer so <see cref="WriteInputFrame"/> can signal it per frame
    /// without a dictionary lookup.</summary>
    public static AutoResetEvent? GetInputEvent(int controllerIndex)
    {
        lock (s_inputViews)
            return s_inputEvents.TryGetValue(controllerIndex, out AutoResetEvent? ev) ? ev : null;
    }

    /// <summary>The wait side of <c>GKMDInputEvent&lt;N&gt;</c>. The block is
    /// in process, so the pump and the writer share one auto-reset event;
    /// the pump is its single waiter.</summary>
    public static AutoResetEvent? OpenInputEventForWait(int controllerIndex) => GetInputEvent(controllerIndex);

    /// <summary>Atomic seqlock write of a new input frame. Single-writer
    /// (the SDK consumer's input loop) → one reader (the device emulator's
    /// input pump) pattern is safe lock-free — the reader retries on SeqNo
    /// mismatch. After publishing the new sequence, signals
    /// <paramref name="eventHandle"/> so the pump can wake immediately
    /// instead of waiting out its timeout.
    ///
    /// <para><c>seqNo</c> is updated in place; the caller maintains it
    /// across frames so it survives mapping resets.</para>
    ///
    /// <para><paramref name="eventHandle"/> may be null if no signaling
    /// event exists. The signal is skipped in that case — the write still
    /// completes normally.</para>
    ///
    /// <para>The stores go through the view's pointer, as HIDMaestro
    /// 2.0.0's writer does. The two lanes take spans, so a caller writes
    /// its frame from where it already is, with no copy into a buffer of
    /// its own first. The seqlock uses one write barrier after the odd
    /// number instead of two full fences: the payload's stores must follow
    /// the odd number (<see cref="Volatile.WriteBarrier"/>), and the even
    /// number is a release store, so the payload is visible before it.</para></summary>
    public static unsafe void WriteInputFrame(IntPtr view, AutoResetEvent? eventHandle, ref uint seqNo,
                                       ReadOnlySpan<byte> data, int dataLen, byte[]? gipData,
                                       int dataOffset = 0,
                                       ReadOnlySpan<byte> extendedData = default, int extendedLen = 0,
                                       AutoResetEvent? companionEvent = null)
    {
        if (view == IntPtr.Zero) throw new AccessViolationException();
        byte* v = (byte*)view;

        // 1. Mark write in progress (odd seqNo). The payload's stores must
        // not become visible before the odd number.
        uint pending = seqNo + 1;
        Volatile.Write(ref *(uint*)v, pending);
        Volatile.WriteBarrier();

        // 2. Write payload (DataSize + Data + GipData + ExtendedReport*).
        // We don't zero the unused data tail past dataLen — the reader
        // reads DataSize and uses only data[0..DataSize-1]; the tail is
        // irrelevant. gipData is null for non-Xbox profiles, so the 14-byte
        // copy is skipped entirely. The block is zero-initialized at create
        // time, so the GIP slice stays zeros.
        //
        // When extendedData is not empty AND extendedLen > 0, the full
        // RID-included extended report goes into ExtendedReportData and
        // ExtendedReportSize is set. Otherwise ExtendedReportSize is
        // explicitly cleared so a previously-armed controller's stale
        // extended bytes don't leak through after a legacy-mode frame.
        // The size word at 278 is not 4-byte aligned, so it takes an
        // unaligned store.
        Unsafe.WriteUnaligned(v + 4, dataLen);
        if (dataLen > 0)
            data.Slice(dataOffset, dataLen).CopyTo(new Span<byte>(v + DATA_OFFSET, dataLen));
        if (gipData != null)
            gipData.AsSpan(0, GIP_DATA_LENGTH).CopyTo(new Span<byte>(v + GIP_DATA_OFFSET, GIP_DATA_LENGTH));
        if (!extendedData.IsEmpty && extendedLen > 0)
        {
            int copyLen = Math.Min(extendedLen, EXTENDED_DATA_CAPACITY);
            extendedData.Slice(0, copyLen).CopyTo(new Span<byte>(v + EXTENDED_DATA_OFFSET, copyLen));
            Unsafe.WriteUnaligned(v + EXTENDED_SIZE_OFFSET, copyLen);
        }
        else
        {
            Unsafe.WriteUnaligned(v + EXTENDED_SIZE_OFFSET, 0);
        }

        // 3. Mark write complete (even seqNo). A volatile write is a
        // release: the payload is visible before the number.
        seqNo = pending + 1;
        Volatile.Write(ref *(uint*)v, seqNo);

        // 4. Wake the input pump and, for a frame that carries GIP bytes,
        // the XUSB companion. Kept out of line so a submit that writes
        // nothing (a state block, a device with ports) pays no call.
        Signal(eventHandle, companionEvent, gipData != null);
    }

    /// <summary>The frame doorbells, once or twice a frame. Never inlined:
    /// a method the JIT inlines a call into sets up the call's frame on
    /// every entry, including calls that signal nothing.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Signal(AutoResetEvent? eventHandle, AutoResetEvent? companionEvent, bool hasGip)
    {
        // Auto-reset event: one successful wait consumes the signal. If the
        // pump is still processing a previous frame the signal stays
        // latched until it returns to waiting.
        eventHandle?.Set();
        if (hasGip) companionEvent?.Set();
    }

    /// <summary>v1.1.40 ring read. Reads the next output packet at
    /// SeqNo <paramref name="lastSeq"/>+1 if available. Returns true with
    /// the packet bytes if a fresh slot is published; false if nothing
    /// new since the last call. On success <paramref name="lastSeq"/>
    /// advances by 1 and the slot's data is copied into <paramref name="dataBuf"/>.
    ///
    /// Caller pumps this in a loop on each poll iteration so all slots
    /// between the previous LastSeen and current Head get drained — see
    /// <see cref="GKController.OutputPollLoop"/>. Pre-1.1.40 single-slot
    /// channel coalesced PID FFB packet bursts; this ring drains them.</summary>
    public static bool TryReadOutputFrame(
        IntPtr view, ref uint lastSeq,
        out byte source, out byte reportId, out int dataSize, byte[] dataBuf)
    {
        source = 0; reportId = 0; dataSize = 0;

        uint head = (uint)Marshal.ReadInt32(view, 0);
        if (head == lastSeq) return false;

        uint nextSeq = lastSeq + 1;

        // Reader fell more than RING_SLOTS behind: oldest packets have
        // been overwritten. Skip ahead to the oldest still-readable slot
        // and continue from there. This is a real lossy edge case — if
        // a consumer is processing significantly slower than the writer
        // is producing, the tail of the burst wins over the head.
        if (head > nextSeq + (uint)OUTPUT_RING_SLOTS - 1)
            nextSeq = head - (uint)OUTPUT_RING_SLOTS + 1;

        int slotIdx = (int)((nextSeq - 1) % (uint)OUTPUT_RING_SLOTS);
        int slotBase = OUTPUT_HEADER_SIZE + slotIdx * OUTPUT_SLOT_SIZE;

        // Per-slot seqlock: read SeqNo, fields, then SeqNo again, and
        // retry on mismatch. The writer RESERVES its sequence via an
        // increment on Head and publishes the slot's SeqNo last, so a
        // mismatch here is either a slot whose reserving writer hasn't
        // finished writing (retry next wake; its doorbell signal follows
        // the publish) or a 64-lap overwrite (handled by the skip-ahead).
        int retries = 4;
        uint slotSeqAfter = 0;
        do
        {
            uint slotSeqBefore = (uint)Marshal.ReadInt32(view, slotBase + OUTPUT_SLOT_OFFSET_SEQNO);
            if (slotSeqBefore != nextSeq)
            {
                // Slot hasn't been written for our expected SeqNo yet.
                // This can happen briefly between the writer incrementing
                // Head and finishing slot.SeqNo. Treat as no new data,
                // try again on next poll.
                return false;
            }
            source = Marshal.ReadByte(view, slotBase + OUTPUT_SLOT_OFFSET_SOURCE);
            reportId = Marshal.ReadByte(view, slotBase + OUTPUT_SLOT_OFFSET_REPORT_ID);
            ushort sz = (ushort)Marshal.ReadInt16(view, slotBase + OUTPUT_SLOT_OFFSET_SIZE);
            if (sz > dataBuf.Length) sz = (ushort)dataBuf.Length;
            dataSize = sz;
            // Bulk Marshal.Copy rather than a per-byte loop. Same
            // per-frame win as the writer side, running on the SDK's
            // output poll thread which fires every 8 ms for every
            // controller.
            if (sz > 0)
                Marshal.Copy(view + slotBase + OUTPUT_SLOT_OFFSET_DATA, dataBuf, 0, sz);
            Thread.MemoryBarrier();
            slotSeqAfter = (uint)Marshal.ReadInt32(view, slotBase + OUTPUT_SLOT_OFFSET_SEQNO);
            if (slotSeqAfter == slotSeqBefore) break;
            // Slot was being rewritten while we read; retry.
        } while (--retries > 0);

        if (slotSeqAfter == 0) return false; // unstable read after retries
        lastSeq = nextSeq;
        return true;
    }

    /// <summary>Releases the input and output blocks for a single controller.
    /// Called when a GKController is disposed. Idempotent.</summary>
    public static unsafe void DestroyController(int controllerIndex)
    {
        // Before any unmap: this controller's poll thread reads the output
        // view every iteration (issue #45).
        StopOutputPump(controllerIndex);

        lock (s_inputViews)
        {
            if (s_inputViews.TryGetValue(controllerIndex, out IntPtr v))
                NativeMemory.Free((void*)v);
            s_inputViews.Remove(controllerIndex);

            if (s_inputEvents.TryGetValue(controllerIndex, out AutoResetEvent? ev))
                ev.Dispose();
            s_inputEvents.Remove(controllerIndex);
        }
        lock (s_outputViews)
        {
            if (s_outputViews.TryGetValue(controllerIndex, out IntPtr v))
                NativeMemory.Free((void*)v);
            s_outputViews.Remove(controllerIndex);

            if (s_outputEvents.TryGetValue(controllerIndex, out AutoResetEvent? oe))
                oe.Dispose();
            s_outputEvents.Remove(controllerIndex);
        }
    }

    /// <summary>Releases all input and output blocks owned by the process.
    /// Safe to call multiple times. Called on process exit and on
    /// GKContext.Dispose.</summary>
    public static unsafe void Cleanup()
    {
        // Before any unmap: poll threads for controllers the caller has NOT
        // disposed are still dereferencing their output views (issue #45).
        // This is the path the device sweep ends on, so a consumer that
        // sweeps while holding a live controller reaches here with threads
        // running.
        StopAllOutputPumps();

        lock (s_inputViews)
        {
            foreach (var v in s_inputViews.Values)
                NativeMemory.Free((void*)v);
            s_inputViews.Clear();
            foreach (var ev in s_inputEvents.Values)
                ev.Dispose();
            s_inputEvents.Clear();
        }
        lock (s_outputViews)
        {
            foreach (var v in s_outputViews.Values)
                NativeMemory.Free((void*)v);
            s_outputViews.Clear();
            foreach (var ev in s_outputEvents.Values)
                ev.Dispose();
            s_outputEvents.Clear();
        }
    }
}
