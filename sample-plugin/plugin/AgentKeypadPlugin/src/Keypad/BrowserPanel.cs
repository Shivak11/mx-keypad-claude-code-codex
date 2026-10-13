namespace Loupedeck.AgentKeypadPlugin
{
    using System;
    using System.IO;
    using System.Runtime.InteropServices;

    // Finds the page open in the built-in browser panel of Codex or Claude.
    // Codex keeps the panel's history in a Chromium History file. Claude keeps nothing on disk,
    // so its panel address is read from the window through macOS accessibility.
    internal static class BrowserPanel
    {
        private const String AX = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
        private const String CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const String ObjC = "/usr/lib/libobjc.A.dylib";

        [DllImport(AX)] private static extern IntPtr AXUIElementCreateApplication(Int32 pid);
        [DllImport(AX)] private static extern Int32 AXUIElementCopyAttributeValue(IntPtr element, IntPtr attribute, out IntPtr value);
        [DllImport(AX)] private static extern Int32 AXUIElementSetAttributeValue(IntPtr element, IntPtr attribute, IntPtr value);
        [DllImport(AX)] private static extern Boolean AXIsProcessTrusted();
        [DllImport(CF)] private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, String text, UInt32 encoding);
        [DllImport(CF)] private static extern Int64 CFArrayGetCount(IntPtr array);
        [DllImport(CF)] private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, Int64 index);
        [DllImport(CF)] private static extern Boolean CFStringGetCString(IntPtr text, Byte[] buffer, Int64 size, UInt32 encoding);
        [DllImport(CF)] private static extern IntPtr CFURLGetString(IntPtr url);
        [DllImport(CF)] private static extern UInt64 CFGetTypeID(IntPtr obj);
        [DllImport(CF)] private static extern UInt64 CFURLGetTypeID();
        [DllImport(CF)] private static extern void CFRelease(IntPtr obj);
        [DllImport(ObjC)] private static extern IntPtr objc_getClass(String name);
        [DllImport(ObjC)] private static extern IntPtr sel_registerName(String name);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern Int32 SendInt(IntPtr receiver, IntPtr selector);

        private const UInt32 Utf8 = 0x08000100;

        private static readonly String CodexHistory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Application Support", "Codex", "Default", "Partitions", "codex-browser-app", "History");

        public static Boolean CanReadClaude()
        {
            try
            {
                return AXIsProcessTrusted();
            }
            catch (Exception)
            {
                return false;
            }
        }

        // The panel's page for the given app, or null with a short reason for the key.
        public static (String url, String why) CurrentUrl(String bundleId)
        {
            try
            {
                if (bundleId == CaptureController.CodexBundle)
                {
                    var url = CodexUrl();
                    return url != null ? (url, null) : (null, "no page open");
                }

                if (bundleId == CaptureController.ClaudeBundle)
                {
                    if (!CanReadClaude())
                    {
                        return (null, "needs access");
                    }

                    var url = ClaudeUrl();
                    return url != null ? (url, null) : (null, "no page open");
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "could not read the browser panel");
            }

            return (null, "no page open");
        }

        // Newest page in Codex's browser panel history. The file is copied first because Codex holds it open.
        private static String CodexUrl()
        {
            if (!File.Exists(CodexHistory))
            {
                return null;
            }

            var copy = Path.Combine(Path.GetTempPath(), $"mx-keypad-codex-history-{Environment.ProcessId}.db");
            try
            {
                // .NET refuses to copy a file another process has locked; the system cp reads it.
                if (MacNative.Run("/bin/cp", CodexHistory, copy) != 0)
                {
                    return null;
                }

                var url = MacNative.Output("/usr/bin/sqlite3", "-readonly", copy,
                    "select url from urls where url like 'http%' order by last_visit_time desc limit 1");
                return String.IsNullOrWhiteSpace(url) ? null : url.Trim();
            }
            finally
            {
                File.Delete(copy);
            }
        }

        private static IntPtr S(String text) => CFStringCreateWithCString(IntPtr.Zero, text, Utf8);

        // Attribute names are created once; creating them per element leaked thousands of strings per press.
        private static readonly IntPtr AXRole = S("AXRole");
        private static readonly IntPtr AXURL = S("AXURL");
        private static readonly IntPtr AXChildren = S("AXChildren");
        private static readonly IntPtr AXWindows = S("AXWindows");
        private static readonly IntPtr AXManual = S("AXManualAccessibility");

        private static void Release(IntPtr obj)
        {
            if (obj != IntPtr.Zero)
            {
                CFRelease(obj);
            }
        }

        private static String Str(IntPtr text)
        {
            if (text == IntPtr.Zero)
            {
                return null;
            }

            var buffer = new Byte[8192];
            return CFStringGetCString(text, buffer, buffer.Length, Utf8) ? System.Text.Encoding.UTF8.GetString(buffer).TrimEnd('\0') : null;
        }

        private static Int32 Pid(String bundleId)
        {
            var apps = Send(objc_getClass("NSRunningApplication"), sel_registerName("runningApplicationsWithBundleIdentifier:"), S(bundleId));
            var first = apps == IntPtr.Zero ? IntPtr.Zero : Send(apps, sel_registerName("firstObject"));
            return first == IntPtr.Zero ? 0 : SendInt(first, sel_registerName("processIdentifier"));
        }

        // Claude's own interface is a web page on claude.ai; any other http page in its windows is the panel.
        private static Boolean IsClaudeUi(String url)
        {
            if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                return true;   // not a web page the panel could be showing
            }

            var host = uri.Host;
            return host is "claude.ai" or "anthropic.com" || host.EndsWith(".claude.ai") || host.EndsWith(".anthropic.com");
        }

        private static String ClaudeUrl()
        {
            var pid = Pid(CaptureController.ClaudeBundle);
            if (pid == 0)
            {
                return null;
            }

            var app = AXUIElementCreateApplication(pid);
            var windows = IntPtr.Zero;
            var visited = 0;
            String found = null;
            try
            {
                // Electron apps only build their accessibility tree when asked to.
                var yes = Marshal.ReadIntPtr(NativeLibrary.GetExport(NativeLibrary.Load(CF), "kCFBooleanTrue"));
                AXUIElementSetAttributeValue(app, AXManual, yes);

                if (AXUIElementCopyAttributeValue(app, AXWindows, out windows) != 0 || windows == IntPtr.Zero)
                {
                    return null;
                }

                for (Int64 i = 0; i < CFArrayGetCount(windows) && found == null; i++)
                {
                    found = Walk(CFArrayGetValueAtIndex(windows, i), 0, ref visited);
                }
            }
            finally
            {
                Release(windows);
                Release(app);
            }

            PluginLog.Info($"Claude panel read: {visited} elements, page {(found == null ? "none" : "found")}");
            return found;
        }

        private static String Walk(IntPtr element, Int32 depth, ref Int32 visited)
        {
            if (depth > 30 || ++visited > 6000)
            {
                return null;
            }

            var role = IntPtr.Zero;
            var isWebArea = AXUIElementCopyAttributeValue(element, AXRole, out role) == 0 && Str(role) == "AXWebArea";
            Release(role);
            if (isWebArea && AXUIElementCopyAttributeValue(element, AXURL, out var value) == 0 && value != IntPtr.Zero)
            {
                var url = CFGetTypeID(value) == CFURLGetTypeID() ? Str(CFURLGetString(value)) : Str(value);
                Release(value);
                if (!IsClaudeUi(url))
                {
                    return url;
                }

                // Claude's own page: keep looking inside it, since the panel may be nested there.
            }

            if (AXUIElementCopyAttributeValue(element, AXChildren, out var children) != 0 || children == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                for (Int64 i = 0; i < CFArrayGetCount(children); i++)
                {
                    var url = Walk(CFArrayGetValueAtIndex(children, i), depth + 1, ref visited);
                    if (url != null)
                    {
                        return url;
                    }
                }

                return null;
            }
            finally
            {
                Release(children);
            }
        }
    }
}
