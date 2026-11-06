using System.Runtime.InteropServices;
using TGDeblocker.Core;

namespace TGDeblocker.Mobile.Services;

internal static class TdMobileJsonClient
{
    public static ITdJsonClient Create()
    {
#if ANDROID || (IOS && TDLIB_IOS_NATIVE)
        return new NativeClient();
#else
        throw new PlatformNotSupportedException("The native TDLib library was not included in this build.");
#endif
    }

#if ANDROID || (IOS && TDLIB_IOS_NATIVE)
    private sealed class NativeClient : ITdJsonClient
    {
#if ANDROID
        private const string Library = "tdjson";
#else
        private const string Library = "__Internal";
#endif
        private readonly IntPtr _client;

        public NativeClient()
        {
            td_json_client_execute(IntPtr.Zero, "{\"@type\":\"setLogVerbosityLevel\",\"new_verbosity_level\":0}");
            _client = td_json_client_create();
            if (_client == IntPtr.Zero)
                throw new InvalidOperationException("TDLib client initialization failed.");
        }

        public void Send(string json) => td_json_client_send(_client, json);

        public string? Receive(double timeoutSeconds) =>
            Marshal.PtrToStringUTF8(td_json_client_receive(_client, timeoutSeconds));

        public void Dispose() => td_json_client_destroy(_client);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern IntPtr td_json_client_create();

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern IntPtr td_json_client_execute(IntPtr client, [MarshalAs(UnmanagedType.LPUTF8Str)] string json);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern void td_json_client_send(IntPtr client, [MarshalAs(UnmanagedType.LPUTF8Str)] string json);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern IntPtr td_json_client_receive(IntPtr client, double timeoutSeconds);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern void td_json_client_destroy(IntPtr client);
    }
#endif
}
