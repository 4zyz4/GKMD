using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace GKMD.Internal;

/// <summary>
/// Per-controller shared-memory IPC between the elevated user-mode SDK
/// process and the USB/IP backend. Owns the
/// pagefile-backed Global\GKMDInput&lt;N&gt; (input) and Global\GKMDOutput&lt;N&gt;
/// (output passthrough) named sections, plus the lock-free seqlock writer
/// for input frames.
///
/// <para>The class is currently <c>static</c> because both the SDK and the
/// in-tree test app go through the same instance — there's only ever one
/// per process. When we want to support multiple <see cref="GKContext"/>
/// instances per process the static state will move onto an instance,
/// but for now this matches how the test app's IPC code worked and lets
/// us extract without churning the test app's call sites.</para>
///
/// <para><b>SDDL note:</b> WUDFHost runs as LocalService and lacks
/// SeCreateGlobalPrivilege, so the kernel-side driver/companion CANNOT
/// create Global\ named sections themselves. The test app / SDK
/// (running elevated) pre-creates the sections with a permissive SDDL
/// that grants LocalService full access, then the driver/companion
/// OpenFileMappings them.</para>
/// </summary>
internal static class SharedMemoryIO
{
    // ── Layout constants — match driver/driver.h ──────────────────────────
    //
    // GKMD_SHARED_INPUT (362 bytes, was 278 pre-v1.3.5):
    //   ULONG  SeqNo                offset 0
    //   ULONG  DataSize             offset 4
    //   UCHAR  Data[256]            offset 8     ← legacy report data, RID stripped
    //   UCHAR  GipData[14]          offset 264
    //   ULONG  ExtendedReportSize   offset 278   ← v1.3.5: 0 = legacy, >0 = use ExtendedReportData
    //   UCHAR  ExtendedReportData[80] offset 282 ← v1.3.5: full RID-included report (Sony BT 0x31 / 0x11)
    //
    // The driver writes EITHER legacy OR extended per frame (mode-switch),
    // not both. SDK clears ExtendedReportSize on legacy frames so the driver
    // doesn't reuse stale extended bytes from a prior arming.
    //
    // GKMD_SHARED_OUTPUT — RING BUFFER as of v1.1.40.
    //   ULONG  Head                        offset 0   (monotonic; total writes by driver)
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

    // Output ring layout (must match driver/driver.h):
    public const int OUTPUT_RING_SLOTS  = 64;
    public const int OUTPUT_SLOT_SIZE   = 4 + 1 + 1 + 2 + 256;     // SeqNo + Source + RID + DataSize + Data[256]
    public const int OUTPUT_HEADER_SIZE = 4 + 4;                   // Head + _Reserved
    public const int SHARED_OUTPUT_SIZE = OUTPUT_HEADER_SIZE + OUTPUT_RING_SLOTS * OUTPUT_SLOT_SIZE;
    public const int OUTPUT_SLOT_OFFSET_SEQNO     = 0;
    public const int OUTPUT_SLOT_OFFSET_SOURCE    = 4;
    public const int OUTPUT_SLOT_OFFSET_REPORT_ID = 5;
    public const int OUTPUT_SLOT_OFFSET_SIZE      = 6;
    public const int OUTPUT_SLOT_OFFSET_DATA      = 8;

    // SDDL granting Local System, Builtin Admins, and LocalService full
    // access plus World read. LocalService is what WUDFHost runs as.
    private const string Sddl =
        "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;LS)(A;;GR;;;WD)";

    private const uint PAGE_READWRITE = 0x04;
    private const uint FILE_MAP_READ  = 0x02;
    private const uint FILE_MAP_WRITE = 0x04;

    // CreateEventW EVENT_MODIFY_STATE | SYNCHRONIZE — open existing named
    // events for SetEvent and waiting from the SDK side.
    private const uint EVENT_MODIFY_STATE        = 0x0002;
    private const uint SYNCHRONIZE               = 0x00100000;

    private static readonly Dictionary<int, IntPtr> s_inputHandles  = new();
    private static readonly Dictionary<int, IntPtr> s_inputViews    = new();
    private static readonly Dictionary<int, IntPtr> s_inputEvents   = new();
    private static readonly Dictionary<int, IntPtr> s_companionInputEvents = new();
    private static readonly Dictionary<int, IntPtr> s_outputHandles = new();
    private static readonly Dictionary<int, IntPtr> s_outputViews   = new();
    private static readonly Dictionary<int, IntPtr> s_outputEvents  = new();

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

    /// <summary>Returns the view pointer for the controller's INPUT section,
    /// creating the section on first call. Thread-safe via per-call lock —
    /// callers can issue concurrent EnsureInputMapping requests for distinct
    /// controllerIndex values without races. Idempotent for the same index.</summary>
    public static IntPtr EnsureInputMapping(int controllerIndex)
    {
        lock (s_inputViews)
        {
            if (s_inputViews.TryGetValue(controllerIndex, out IntPtr existing))
                return existing;

            string name = $@"Global\GKMDInput{controllerIndex}";
            (IntPtr h, IntPtr view) = CreateSection(name, SHARED_INPUT_SIZE);

            // Zero-init: pagefile-backed sections start zero already, but we
            // explicitly write SeqNo=0 so a stale section from a previous
            // run (under the same name) doesn't carry forward a non-zero
            // sequence number that the driver would treat as "no change".
            // Bulk Marshal.Copy from a freshly allocated zero buffer is one
            // P/Invoke; the prior per-byte loop was 278 P/Invokes per setup.
            Marshal.Copy(new byte[SHARED_INPUT_SIZE], 0, view, SHARED_INPUT_SIZE);

            // Companion signaling event. Auto-reset (manual_reset=FALSE), not
            // initially set. The USB/IP device emulator's input pump waits on
            // this with a 50ms safety timeout to replace the old 1ms busy poll.
            // SDDL matches the section so WUDFHost (LocalService) can open it.
            string evName = $@"Global\GKMDInputEvent{controllerIndex}";
            IntPtr ev = CreateNamedEvent(evName);

            // Companion input doorbell (perf audit 2026-07-21). The XUSB
            // companion's WAIT_FOR_INPUT pump was purely 8 ms
            // timer-quantized, which put a 0-8 ms (median ~4 ms) phase
            // delay on the WGI/GameInput input path and 125 idle wakes/s
            // per Xbox controller. A SECOND named auto-reset event (the
            // main input event is consumed by the USB/IP device emulator's
            // input pump, an auto-reset event cannot serve two waiters) lets the
            // companion complete a parked WAIT_FOR_INPUT at frame
            // arrival. Old companions never open it: timer fallback.
            string cevName = $@"Global\GKMDCompanionInputEvent{controllerIndex}";
            IntPtr cev = CreateNamedEvent(cevName);
            s_companionInputEvents[controllerIndex] = cev;

            s_inputHandles[controllerIndex] = h;
            s_inputViews[controllerIndex] = view;
            s_inputEvents[controllerIndex] = ev;
            return view;
        }
    }

    /// <summary>Try to open the driver-created output-ring doorbell
    /// <c>Global\GKMDOutputEvent&lt;N&gt;</c> (issue #34). The DRIVER
    /// creates this event (companion opens-or-creates the same name), so
    /// open success doubles as capability detection: success means the
    /// installed driver signals per published packet and the reader can
    /// block on it; failure means an older driver and the caller keeps
    /// the 8 ms poll cadence. Returns IntPtr.Zero when absent. The caller
    /// owns the returned handle.</summary>
    public static IntPtr TryOpenOutputEvent(int controllerIndex)
    {
        return OpenEventW(SYNCHRONIZE, false,
            $@"Global\GKMDOutputEvent{controllerIndex}");
    }

    /// <summary>Companion input doorbell handle for
    /// <c>Global\GKMDCompanionInputEvent&lt;N&gt;</c> (created in
    /// EnsureInputMapping). IntPtr.Zero when the mapping was never
    /// created in this process.</summary>
    public static IntPtr GetCompanionInputEvent(int controllerIndex)
    {
        lock (s_inputViews)
            return s_companionInputEvents.TryGetValue(controllerIndex, out var ev) ? ev : IntPtr.Zero;
    }

    /// <summary>Create (or return) the output-ring doorbell
    /// <c>Global\GKMDOutputEvent&lt;N&gt;</c> from the SDK side.
    /// The USB/IP backend (issue #39) has no external driver, so the
    /// in-process device emulator creates it here, with the same SDDL,
    /// BEFORE the GKController's OutputPollLoop calls
    /// <see cref="TryOpenOutputEvent"/>, so the reader comes up in
    /// event mode. Cached per index and closed by DestroyController.</summary>
    public static IntPtr EnsureOutputEvent(int controllerIndex)
    {
        lock (s_outputViews)
        {
            if (s_outputEvents.TryGetValue(controllerIndex, out IntPtr existing))
                return existing;
            IntPtr ev = CreateNamedEvent($@"Global\GKMDOutputEvent{controllerIndex}");
            s_outputEvents[controllerIndex] = ev;
            return ev;
        }
    }

    /// <summary>Open an independent SYNCHRONIZE handle to
    /// <c>Global\GKMDInputEvent&lt;N&gt;</c> for a waiter. The handle
    /// EnsureInputMapping caches is the writer's SetEvent handle; the
    /// USB/IP device emulator's input pump is the auto-reset event's sole
    /// waiter and owns the returned handle.</summary>
    public static IntPtr OpenInputEventForWait(int controllerIndex)
    {
        return OpenEventW(SYNCHRONIZE | EVENT_MODIFY_STATE, false,
            $@"Global\GKMDInputEvent{controllerIndex}");
    }

    /// <summary>Returns the view pointer for the controller's OUTPUT section,
    /// creating the section on first call. The backend attaches to
    /// this section read-write to publish captured rumble/haptics/FFB/LED.</summary>
    public static IntPtr EnsureOutputMapping(int controllerIndex)
    {
        lock (s_outputViews)
        {
            if (s_outputViews.TryGetValue(controllerIndex, out IntPtr existing))
                return existing;

            string name = $@"Global\GKMDOutput{controllerIndex}";
            (IntPtr h, IntPtr view) = CreateSection(name, SHARED_OUTPUT_SIZE);

            // Zero-init: pagefile sections start zero, BUT a residual kernel
            // object from a prior SDK process (kept alive by a still-loaded
            // backend view) may retain a non-zero SeqNo and stale Data. Without
            // this, a fresh OutputPollLoop sees `SeqNo != lastSeq(0)` on its
            // first sample and replays prior-session FFB as a brand-new
            // OutputReceived packet — on repeat if a consumer process's
            // XInput/HID handle still talks to the ghost slot.
            // Bulk Marshal.Copy from a freshly allocated zero buffer is one
            // P/Invoke; the prior per-byte loop was ~17K P/Invokes per setup
            // (SHARED_OUTPUT_SIZE = 16,904 bytes).
            Marshal.Copy(new byte[SHARED_OUTPUT_SIZE], 0, view, SHARED_OUTPUT_SIZE);

            s_outputHandles[controllerIndex] = h;
            s_outputViews[controllerIndex] = view;
            return view;
        }
    }

/// <summary>Returns the signaling event handle for a controller's input
    /// section, or <see cref="IntPtr.Zero"/> if no mapping has been created
    /// for that index yet. Used by <see cref="GKController"/> to cache the
    /// handle alongside the view pointer so <see cref="WriteInputFrame"/>
    /// can signal it per frame without a dictionary lookup.</summary>
    public static IntPtr GetInputEvent(int controllerIndex)
    {
        lock (s_inputViews)
        {
            return s_inputEvents.TryGetValue(controllerIndex, out IntPtr ev) ? ev : IntPtr.Zero;
        }
    }

    /// <summary>Atomic seqlock write of a new input frame. Single-writer
    /// (the SDK consumer's input loop) → many-readers (driver + companion)
    /// pattern is safe lock-free — readers retry on SeqNo mismatch. After
    /// publishing the new sequence, signals <paramref name="eventHandle"/>
    /// so the driver's worker thread can wake immediately instead of
    /// busy-polling the section.
    ///
    /// <para><c>seqNo</c> is updated in place; the caller maintains it
    /// across frames so it survives mapping resets.</para>
    ///
    /// <para><paramref name="eventHandle"/> may be <see cref="IntPtr.Zero"/>
    /// if no signaling event exists (e.g. older driver that still polls
    /// without opening the event). The SetEvent call is skipped in that
    /// case — the write still completes normally.</para></summary>
    public static void WriteInputFrame(IntPtr view, IntPtr eventHandle, ref uint seqNo,
                                       byte[] data, int dataLen, byte[]? gipData,
                                       int dataOffset = 0,
                                       byte[]? extendedData = null, int extendedLen = 0,
                                       IntPtr companionEvent = default)
    {
        // 1. Mark write in progress (odd seqNo)
        uint pending = seqNo + 1;
        Marshal.WriteInt32(view, 0, (int)pending);
        Thread.MemoryBarrier();

        // 2. Write payload (DataSize + Data + GipData + ExtendedReport*).
        // v1.3.0 — bulk Marshal.Copy replaces the prior per-byte
        // Marshal.WriteByte loops, which were 256 + 14 = 270 P/Invoke
        // calls per frame. At 250 Hz × 6 controllers that was ~400 000
        // P/Invokes/sec; a single bulk copy per region is two
        // P/Invokes/frame total. We don't zero the unused data tail past
        // dataLen — backend/consumer reads DataSize and uses only
        // data[0..DataSize-1]; the tail is irrelevant. T26-2 — gipData
        // is null for non-Xbox profiles (no XUSB companion bound), so we
        // can skip the 14-byte copy entirely. Section is zero-initialized
        // at create time, so the GIP slice stays zeros.
        //
        // v1.3.5 — when extendedData != null AND extendedLen > 0, copy
        // the full RID-included extended report into ExtendedReportData
        // and set ExtendedReportSize. Backend branches on
        // ExtendedReportSize > 0 and emits ExtendedReportData verbatim
        // (no FirstInputReportId prepend). When extendedData is null we
        // explicitly clear ExtendedReportSize so a previously-armed
        // controller's stale extended bytes don't leak through after
        // a legacy-mode frame (e.g. if armOn fires were ever rolled back
        // — currently they aren't, but cheap insurance).
        Marshal.WriteInt32(view, 4, dataLen);
        if (dataLen > 0)
            Marshal.Copy(data, dataOffset, view + DATA_OFFSET, dataLen);
        if (gipData != null)
            Marshal.Copy(gipData, 0, view + GIP_DATA_OFFSET, GIP_DATA_LENGTH);
        if (extendedData != null && extendedLen > 0)
        {
            int copyLen = Math.Min(extendedLen, EXTENDED_DATA_CAPACITY);
            Marshal.Copy(extendedData, 0, view + EXTENDED_DATA_OFFSET, copyLen);
            Marshal.WriteInt32(view, EXTENDED_SIZE_OFFSET, copyLen);
        }
        else
        {
            // Mandatory per-frame clear: named sections (Global\GKMDInput<N>)
            // can be opened concurrently by other processes (CreateFileMappingW
            // returns existing handle for any caller passing the same name + SDDL).
            // Across emulate-app restarts the SDK re-zeros at create time, but
            // BETWEEN restarts a foreign writer could have left ExtendedReportSize
            // > 0 + ExtendedReportData populated; with the legacy path running
            // (USB Sony, generic profiles) that stale extended buffer would be
            // emitted by the backend, breaking input. Cost is one P/Invoke per
            // frame.
            Marshal.WriteInt32(view, EXTENDED_SIZE_OFFSET, 0);
        }

        // 3. Mark write complete (even seqNo)
        Thread.MemoryBarrier();
        seqNo = pending + 1;
        Marshal.WriteInt32(view, 0, (int)seqNo);

        // 4. Wake the backend's input pump. Auto-reset event: one
        // successful wait consumes the signal. If the backend is still
        // processing a previous frame the signal stays latched until
        // it returns to WaitForMultipleObjects.
        if (eventHandle != IntPtr.Zero)
            SetEvent(eventHandle);

        // 5. Companion doorbell (perf audit 2026-07-21): only frames that
        // carry GIP bytes feed the XUSB companion's WAIT_FOR_INPUT pump,
        // so non-Xbox profiles pay nothing here.
        if (companionEvent != IntPtr.Zero && gipData != null)
            SetEvent(companionEvent);
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
        // a consumer is processing significantly slower than the driver
        // is producing, the tail of the burst wins over the head.
        if (head > nextSeq + (uint)OUTPUT_RING_SLOTS - 1)
            nextSeq = head - (uint)OUTPUT_RING_SLOTS + 1;

        int slotIdx = (int)((nextSeq - 1) % (uint)OUTPUT_RING_SLOTS);
        int slotBase = OUTPUT_HEADER_SIZE + slotIdx * OUTPUT_SLOT_SIZE;

        // Per-slot seqlock: read SeqNo, fields, then SeqNo again, and
        // retry on mismatch. Since the audit of #34, producers RESERVE their
        // sequence via InterlockedIncrement on Head and publish the
        // slot's SeqNo last, so a mismatch here is either a slot whose
        // reserving producer hasn't finished writing (retry next wake;
        // its doorbell signal follows the publish) or a 64-lap overwrite
        // (handled by the skip-ahead above).
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
            // v1.3.0 — bulk Marshal.Copy replaces the prior per-byte
            // Marshal.ReadByte loop. Same per-frame win as the writer side
            // (1 P/Invoke instead of N), running on the SDK's output poll
            // thread which fires every 8 ms for every controller.
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

    /// <summary>Releases the input and output sections for a single controller.
    /// Called when an GKController is disposed. Idempotent.</summary>
    public static void DestroyController(int controllerIndex)
    {
        // Before any unmap: this controller's poll thread reads the output
        // view every iteration (issue #45).
        StopOutputPump(controllerIndex);

        lock (s_inputViews)
        {
            if (s_inputViews.TryGetValue(controllerIndex, out IntPtr v) && v != IntPtr.Zero)
                UnmapViewOfFile(v);
            s_inputViews.Remove(controllerIndex);

            if (s_inputHandles.TryGetValue(controllerIndex, out IntPtr h) && h != IntPtr.Zero)
                CloseHandle(h);
            s_inputHandles.Remove(controllerIndex);

            if (s_inputEvents.TryGetValue(controllerIndex, out IntPtr ev) && ev != IntPtr.Zero)
                CloseHandle(ev);
            s_inputEvents.Remove(controllerIndex);
        }
        lock (s_outputViews)
        {
            if (s_outputViews.TryGetValue(controllerIndex, out IntPtr v) && v != IntPtr.Zero)
                UnmapViewOfFile(v);
            s_outputViews.Remove(controllerIndex);

            if (s_outputHandles.TryGetValue(controllerIndex, out IntPtr h) && h != IntPtr.Zero)
                CloseHandle(h);
            s_outputHandles.Remove(controllerIndex);

            if (s_outputEvents.TryGetValue(controllerIndex, out IntPtr oe) && oe != IntPtr.Zero)
                CloseHandle(oe);
            s_outputEvents.Remove(controllerIndex);
        }
    }

    /// <summary>Releases all input and output sections owned by the process.
    /// Safe to call multiple times. Called on process exit and on
    /// GKContext.Dispose.</summary>
    public static void Cleanup()
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
                if (v != IntPtr.Zero) UnmapViewOfFile(v);
            s_inputViews.Clear();
            foreach (var h in s_inputHandles.Values)
                if (h != IntPtr.Zero) CloseHandle(h);
            s_inputHandles.Clear();
            foreach (var ev in s_inputEvents.Values)
                if (ev != IntPtr.Zero) CloseHandle(ev);
            s_inputEvents.Clear();
            foreach (var ev in s_companionInputEvents.Values)
                if (ev != IntPtr.Zero) CloseHandle(ev);
            s_companionInputEvents.Clear();
        }
        lock (s_outputViews)
        {
            foreach (var v in s_outputViews.Values)
                if (v != IntPtr.Zero) UnmapViewOfFile(v);
            s_outputViews.Clear();
            foreach (var h in s_outputHandles.Values)
                if (h != IntPtr.Zero) CloseHandle(h);
            s_outputHandles.Clear();
            foreach (var ev in s_outputEvents.Values)
                if (ev != IntPtr.Zero) CloseHandle(ev);
            s_outputEvents.Clear();
        }
    }

    // ── private helpers ───────────────────────────────────────────────────

    private static (IntPtr handle, IntPtr view) CreateSection(string name, int size)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                Sddl, 1, out IntPtr sd, IntPtr.Zero))
            throw new Win32Exception();

        SECURITY_ATTRIBUTES sa = new()
        {
            nLength = (uint)Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            lpSecurityDescriptor = sd,
            bInheritHandle = 0,
        };
        IntPtr saPtr = Marshal.AllocHGlobal(Marshal.SizeOf<SECURITY_ATTRIBUTES>());
        Marshal.StructureToPtr(sa, saPtr, false);

        IntPtr hMap;
        try
        {
            hMap = CreateFileMappingW(new IntPtr(-1), saPtr,
                PAGE_READWRITE, 0, (uint)size, name);
        }
        finally
        {
            Marshal.FreeHGlobal(saPtr);
            LocalFree(sd);
        }

        if (hMap == IntPtr.Zero)
            throw new Win32Exception();

        IntPtr view = MapViewOfFile(hMap, FILE_MAP_WRITE | FILE_MAP_READ,
            0, 0, (UIntPtr)size);
        if (view == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            CloseHandle(hMap);
            throw new Win32Exception(err);
        }
        return (hMap, view);
    }

    /// <summary>Creates a named auto-reset event under the same SDDL used
    /// for the sections so WUDFHost (LocalService) can OpenEvent it. Auto
    /// reset so a single Wait consumes the signal; manual_reset=FALSE means
    /// the dwFlags argument to CreateEventExW is 0 (not MANUAL_RESET).</summary>
    private static IntPtr CreateNamedEvent(string name)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                Sddl, 1, out IntPtr sd, IntPtr.Zero))
            throw new Win32Exception();

        SECURITY_ATTRIBUTES sa = new()
        {
            nLength = (uint)Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            lpSecurityDescriptor = sd,
            bInheritHandle = 0,
        };
        IntPtr saPtr = Marshal.AllocHGlobal(Marshal.SizeOf<SECURITY_ATTRIBUTES>());
        Marshal.StructureToPtr(sa, saPtr, false);

        IntPtr ev;
        try
        {
            // dwFlags = 0 → auto-reset, not initially set.
            ev = CreateEventExW(saPtr, name, 0, EVENT_MODIFY_STATE | SYNCHRONIZE);
        }
        finally
        {
            Marshal.FreeHGlobal(saPtr);
            LocalFree(sd);
        }

        if (ev == IntPtr.Zero)
            throw new Win32Exception();
        return ev;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public uint nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileMappingW(IntPtr hFile, IntPtr lpAttributes,
        uint flProtect, uint dwMaximumSizeHigh, uint dwMaximumSizeLow, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject, uint dwDesiredAccess,
        uint dwFileOffsetHigh, uint dwFileOffsetLow, UIntPtr dwNumberOfBytesToMap);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "CloseHandle")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string StringSecurityDescriptor, uint StringSDRevision,
        out IntPtr SecurityDescriptor, IntPtr SecurityDescriptorSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEventExW(IntPtr lpEventAttributes, string lpName,
        uint dwFlags, uint dwDesiredAccess);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenEventW(uint dwDesiredAccess, bool bInheritHandle,
        string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetEvent(IntPtr hEvent);
}
