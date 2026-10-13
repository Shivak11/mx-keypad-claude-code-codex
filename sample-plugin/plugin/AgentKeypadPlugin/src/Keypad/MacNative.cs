namespace Loupedeck.AgentKeypadPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Runtime.InteropServices;
    using System.Threading;

    // Small macOS calls the Logitech SDK does not offer: the frontmost app, the fn key,
    // the clipboard change count and activating an app by bundle id.
    internal static class MacNative
    {
        private const String ObjC = "/usr/lib/libobjc.A.dylib";
        private const String CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

        [DllImport(ObjC)] private static extern IntPtr objc_getClass(String name);
        [DllImport(ObjC)] private static extern IntPtr sel_registerName(String name);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern Int64 SendLong(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtr(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendIndex(IntPtr receiver, IntPtr selector, UInt64 index);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendBytes(IntPtr receiver, IntPtr selector, Byte[] bytes, UInt64 length);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern Boolean SendTwo(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern Boolean SendBool(IntPtr receiver, IntPtr selector, IntPtr a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendStr(IntPtr receiver, IntPtr selector, String utf8);
        [DllImport(ObjC)] private static extern IntPtr objc_autoreleasePoolPush();
        [DllImport(ObjC)] private static extern void objc_autoreleasePoolPop(IntPtr pool);

        [DllImport(CG)] private static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, UInt16 keycode, Boolean keyDown);
        [DllImport(CG)] private static extern void CGEventSetFlags(IntPtr evt, UInt64 flags);
        [DllImport(CG)] private static extern void CGEventPost(UInt32 tap, IntPtr evt);
        [DllImport(CG)] private static extern void CGEventSetType(IntPtr evt, UInt32 type);
        [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static extern void CFRelease(IntPtr obj);

        private const UInt16 KeyFn = 63;
        private const UInt16 KeyShift = 56;
        private const UInt16 KeyReturn = 36;
        private const UInt64 FlagFn = 0x800000;
        private const UInt64 FlagShift = 0x20000;

        private static String NsString(IntPtr ns) =>
            ns == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(Send(ns, sel_registerName("UTF8String")));

        // Bundle id of the app in front, such as com.anthropic.claudefordesktop.
        public static String FrontmostBundleId()
        {
            try
            {
                var ws = Send(objc_getClass("NSWorkspace"), sel_registerName("sharedWorkspace"));
                var app = Send(ws, sel_registerName("frontmostApplication"));
                return app == IntPtr.Zero ? null : NsString(Send(app, sel_registerName("bundleIdentifier")));
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static IntPtr Sel(String name) => sel_registerName(name);

        // NSPasteboard lives in AppKit. Logi Plugin Service loads it already; loading it here makes sure.
        private static readonly Boolean AppKit = NativeLibrary.TryLoad("/System/Library/Frameworks/AppKit.framework/AppKit", out _);

        // The general pasteboard, or a named one (used by the clipboard test so the real clipboard is left alone).
        private static IntPtr Pasteboard(String name) =>
            name == null
                ? Send(objc_getClass("NSPasteboard"), Sel("generalPasteboard"))
                : SendPtr(objc_getClass("NSPasteboard"), Sel("pasteboardWithName:"), SendStr(objc_getClass("NSString"), Sel("stringWithUTF8String:"), name));

        // Every item on the clipboard with every type it offers (plain text, rich text, image, file, ...).
        public static List<List<(String type, Byte[] data)>> SaveClipboard(String pasteboard = null)
        {
            if (!AppKit)
            {
                throw new InvalidOperationException("AppKit is not available");
            }

            var pool = objc_autoreleasePoolPush();
            try
            {
                var saved = new List<List<(String, Byte[])>>();
                var pb = Pasteboard(pasteboard);
                if (pb == IntPtr.Zero)
                {
                    throw new InvalidOperationException("no pasteboard");
                }

                var items = Send(pb, Sel("pasteboardItems"));
                var count = items == IntPtr.Zero ? 0 : SendLong(items, Sel("count"));
                for (var i = 0L; i < count; i++)
                {
                    var item = SendIndex(items, Sel("objectAtIndex:"), (UInt64)i);
                    var types = Send(item, Sel("types"));
                    var entry = new List<(String, Byte[])>();
                    for (var t = 0L; t < SendLong(types, Sel("count")); t++)
                    {
                        var type = SendIndex(types, Sel("objectAtIndex:"), (UInt64)t);
                        var data = SendPtr(item, Sel("dataForType:"), type);
                        var typeName = NsString(type);
                        if (data == IntPtr.Zero || typeName == null)
                        {
                            continue;
                        }

                        var bytes = new Byte[SendLong(data, Sel("length"))];
                        if (bytes.Length > 0)
                        {
                            Marshal.Copy(Send(data, Sel("bytes")), bytes, 0, bytes.Length);
                        }

                        entry.Add((typeName, bytes));
                    }

                    saved.Add(entry);
                }

                return saved;
            }
            finally
            {
                objc_autoreleasePoolPop(pool);
            }
        }

        // Puts back what SaveClipboard returned. Returns false if the clipboard could not be written.
        public static Boolean RestoreClipboard(List<List<(String type, Byte[] data)>> saved, String pasteboard = null)
        {
            if (!AppKit || saved == null)
            {
                return false;
            }

            var pool = objc_autoreleasePoolPush();
            try
            {
                var pb = Pasteboard(pasteboard);
                if (pb == IntPtr.Zero)
                {
                    return false;
                }

                SendLong(pb, Sel("clearContents"));
                if (saved.Count == 0)
                {
                    return true;
                }

                // Any nil here would raise an Objective-C exception, which ends the whole plugin service, so check each one.
                var array = Send(objc_getClass("NSMutableArray"), Sel("array"));
                var itemClass = objc_getClass("NSPasteboardItem");
                if (array == IntPtr.Zero || itemClass == IntPtr.Zero)
                {
                    return false;
                }

                foreach (var entry in saved)
                {
                    var item = Send(Send(itemClass, Sel("alloc")), Sel("init"));
                    if (item == IntPtr.Zero)
                    {
                        return false;
                    }

                    foreach (var (type, data) in entry)
                    {
                        var nsData = SendBytes(objc_getClass("NSData"), Sel("dataWithBytes:length:"), data ?? Array.Empty<Byte>(), (UInt64)(data?.Length ?? 0));
                        var nsType = type == null ? IntPtr.Zero : SendStr(objc_getClass("NSString"), Sel("stringWithUTF8String:"), type);
                        if (nsData != IntPtr.Zero && nsType != IntPtr.Zero)
                        {
                            SendTwo(item, Sel("setData:forType:"), nsData, nsType);
                        }
                    }

                    SendPtr(array, Sel("addObject:"), item);
                    Send(item, Sel("release"));
                }

                return SendBool(pb, Sel("writeObjects:"), array);
            }
            finally
            {
                objc_autoreleasePoolPop(pool);
            }
        }

        // Increases each time anything is copied, including Doing's transcript.
        public static Int64 ClipboardChangeCount()
        {
            try
            {
                var pb = Send(objc_getClass("NSPasteboard"), sel_registerName("generalPasteboard"));
                return SendLong(pb, sel_registerName("changeCount"));
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private static void Key(UInt16 code, Boolean down, UInt64 flags)
        {
            var e = CGEventCreateKeyboardEvent(IntPtr.Zero, code, down);
            if (e == IntPtr.Zero)
            {
                return;
            }

            // fn and Shift are modifier keys: macOS reports them as a modifier change, not a key press.
            if (code == KeyFn || code == KeyShift)
            {
                CGEventSetType(e, 12);   // kCGEventFlagsChanged
            }

            CGEventSetFlags(e, flags);
            CGEventPost(0, e);
            CFRelease(e);
        }

        // Doing's hands-free start: hold fn, tap Shift, release fn. Recording then continues until fn is pressed again.
        public static void FnHandsFree()
        {
            Key(KeyFn, true, FlagFn);
            Thread.Sleep(120);
            Key(KeyShift, true, FlagFn | FlagShift);
            Thread.Sleep(60);
            Key(KeyShift, false, FlagFn);
            Thread.Sleep(120);
            Key(KeyFn, false, 0);
        }

        // A single fn press, which stops a hands-free recording in Doing.
        public static void FnTap()
        {
            Key(KeyFn, true, FlagFn);
            Thread.Sleep(80);
            Key(KeyFn, false, 0);
        }

        public static void Return()
        {
            Key(KeyReturn, true, 0);
            Key(KeyReturn, false, 0);
        }

        public static void Activate(String bundleId) => Run("/usr/bin/open", "-b", bundleId);

        public static Int32 Run(String file, params String[] args)
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi);
            p.WaitForExit(15000);
            return p.HasExited ? p.ExitCode : -1;
        }

        public static String Output(String file, params String[] args)
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi);
            var text = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(10000))
            {
                try
                {
                    p.Kill();
                }
                catch (Exception)
                {
                }

                return null;
            }

            return p.ExitCode == 0 ? text.Trim() : null;
        }
    }
}
