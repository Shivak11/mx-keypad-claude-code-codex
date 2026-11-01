namespace Loupedeck.AgentKeypadPlugin
{
    using System;
    using System.Runtime.InteropServices;

    // Keeps only the AX element and selection for this capture. Never logs field contents.
    internal sealed class FocusedField : IDisposable
    {
        private const String AX = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
        private const String CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const String ObjC = "/usr/lib/libobjc.A.dylib";
        private const UInt32 Utf8 = 0x08000100;
        [DllImport(AX)] private static extern IntPtr AXUIElementCreateApplication(Int32 pid);
        [DllImport(AX)] private static extern Int32 AXUIElementCopyAttributeValue(IntPtr e, IntPtr a, out IntPtr v);
        [DllImport(AX)] private static extern Int32 AXUIElementSetAttributeValue(IntPtr e, IntPtr a, IntPtr v);
        [DllImport(CF)] private static extern IntPtr CFStringCreateWithCString(IntPtr a, String s, UInt32 encoding);
        [DllImport(CF)] private static extern Boolean CFStringGetCString(IntPtr s, Byte[] b, Int64 n, UInt32 encoding);
        [DllImport(CF)] private static extern UInt64 CFGetTypeID(IntPtr o);
        [DllImport(CF)] private static extern UInt64 CFStringGetTypeID();
        [DllImport(CF)] private static extern Boolean CFEqual(IntPtr a, IntPtr b);
        [DllImport(CF)] private static extern void CFRelease(IntPtr o);
        [DllImport(ObjC)] private static extern IntPtr objc_getClass(String name);
        [DllImport(ObjC)] private static extern IntPtr sel_registerName(String name);
        [DllImport(ObjC, EntryPoint="objc_msgSend")] private static extern IntPtr Send(IntPtr obj, IntPtr selector);
        [DllImport(ObjC, EntryPoint="objc_msgSend")] private static extern IntPtr SendArg(IntPtr obj, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint="objc_msgSend")] private static extern Int32 SendInt(IntPtr obj, IntPtr selector);
        private static readonly IntPtr Focused = S("AXFocusedUIElement"), Role = S("AXRole"), Value = S("AXValue"), Selection = S("AXSelectedTextRange"), IsFocused = S("AXFocused"), Manual = S("AXManualAccessibility");
        private static readonly IntPtr True = Marshal.ReadIntPtr(NativeLibrary.GetExport(NativeLibrary.Load(CF), "kCFBooleanTrue"));
        private IntPtr _app, _field, _selection;
        private static IntPtr S(String s) => CFStringCreateWithCString(IntPtr.Zero, s, Utf8);
        private static void Release(IntPtr p) { if (p != IntPtr.Zero) CFRelease(p); }
        private static String Text(IntPtr p)
        {
            if (p == IntPtr.Zero || CFGetTypeID(p) != CFStringGetTypeID()) return null;
            var bytes = new Byte[2 * 1024 * 1024];
            return CFStringGetCString(p, bytes, bytes.Length, Utf8) ? System.Text.Encoding.UTF8.GetString(bytes).TrimEnd('\0') : null;
        }
        public static FocusedField Capture(String bundle)
        {
            if (String.IsNullOrEmpty(bundle)) return null;
            var name = S(bundle);
            IntPtr app = IntPtr.Zero, field = IntPtr.Zero, role = IntPtr.Zero, selection = IntPtr.Zero;
            try
            {
                var apps = SendArg(objc_getClass("NSRunningApplication"), sel_registerName("runningApplicationsWithBundleIdentifier:"), name);
                var running = apps == IntPtr.Zero ? IntPtr.Zero : Send(apps, sel_registerName("firstObject"));
                var pid = running == IntPtr.Zero ? 0 : SendInt(running, sel_registerName("processIdentifier"));
                if (pid == 0) return null;
                app = AXUIElementCreateApplication(pid);
                AXUIElementSetAttributeValue(app, Manual, True);
                if (AXUIElementCopyAttributeValue(app, Focused, out field) != 0 || field == IntPtr.Zero || AXUIElementCopyAttributeValue(field, Role, out role) != 0) return null;
                if (Text(role) is not ("AXTextArea" or "AXTextField" or "AXComboBox" or "AXSearchField")) return null;
                AXUIElementCopyAttributeValue(field, Selection, out selection);
                var result = new FocusedField { _app = app, _field = field, _selection = selection };
                app = field = selection = IntPtr.Zero;
                return result;
            }
            finally { Release(name); Release(role); Release(selection); Release(field); Release(app); }
        }
        public Boolean Restore()
        {
            lock (this)
            {
                if (_field == IntPtr.Zero || AXUIElementSetAttributeValue(_field, IsFocused, True) != 0) return false;
                if (_selection != IntPtr.Zero) AXUIElementSetAttributeValue(_field, Selection, _selection);
                if (AXUIElementCopyAttributeValue(_app, Focused, out var current) != 0 || current == IntPtr.Zero) return false;
                try { return CFEqual(current, _field); } finally { Release(current); }
            }
        }
        public String ReadValue()
        {
            lock (this)
            {
                if (_field == IntPtr.Zero || AXUIElementCopyAttributeValue(_field, Value, out var value) != 0 || value == IntPtr.Zero) return null;
                try { return Text(value); } finally { Release(value); }
            }
        }
        public void Dispose() { lock (this) { Release(_selection); Release(_field); Release(_app); _selection = _field = _app = IntPtr.Zero; } }
    }
}
