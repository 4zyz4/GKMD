using System;
using System.Diagnostics;

namespace GKMD.Internal.Usbip;

/// <summary>Raises the public audio events that fire on the USB/IP device's
/// threads. Each handler runs in turn. An exception one throws is caught,
/// written to the debug log and dropped: it ends neither the audio pump nor
/// the device's connection, and the handlers after it still run.</summary>
internal static class AudioEventRaise
{
    public static void Each<T1>(Action<T1>? handler, T1 a, string eventName)
    {
        foreach (var h in Delegate.EnumerateInvocationList(handler))
        {
            try { h(a); }
            catch (Exception ex) { Dropped(eventName, ex); }
        }
    }

    public static void Each<T1, T2>(Action<T1, T2>? handler, T1 a, T2 b, string eventName)
    {
        foreach (var h in Delegate.EnumerateInvocationList(handler))
        {
            try { h(a, b); }
            catch (Exception ex) { Dropped(eventName, ex); }
        }
    }

    public static void Each<T1, T2, T3, T4>(Action<T1, T2, T3, T4>? handler, T1 a, T2 b, T3 c, T4 d, string eventName)
    {
        foreach (var h in Delegate.EnumerateInvocationList(handler))
        {
            try { h(a, b, c, d); }
            catch (Exception ex) { Dropped(eventName, ex); }
        }
    }

    public static void Each<TArgs>(EventHandler<TArgs>? handler, object sender, TArgs args, string eventName)
    {
        foreach (var h in Delegate.EnumerateInvocationList(handler))
        {
            try { h(sender, args); }
            catch (Exception ex) { Dropped(eventName, ex); }
        }
    }

    private static void Dropped(string eventName, Exception ex) =>
        Debug.WriteLine($"a {eventName} handler threw {ex.GetType().FullName}: {ex.Message}");
}
