using System;
using System.Runtime.InteropServices;
using System.Text;
using ScreenQA.Native;

namespace ScreenQA.Services;

/// <summary>
/// Шифрует ключи API через Windows DPAPI (область текущего пользователя).
/// Расшифровать значение может только тот же пользователь Windows на этом компьютере.
/// </summary>
internal static class SecretProtector
{
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return string.Empty;
        var bytes = Encoding.UTF8.GetBytes(plain);
        try
        {
            return Convert.ToBase64String(Transform(bytes, protect: true));
        }
        finally
        {
            Array.Clear(bytes);
        }
    }

    public static string Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return string.Empty;
        try
        {
            var plain = Transform(Convert.FromBase64String(protectedBase64), protect: false);
            try
            {
                return Encoding.UTF8.GetString(plain);
            }
            finally
            {
                Array.Clear(plain);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось расшифровать сохранённый ключ: {ex.GetType().Name}");
            return string.Empty;
        }
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inBlob = new NativeMethods.DATA_BLOB { cbData = input.Length, pbData = Marshal.AllocHGlobal(input.Length) };
        var outBlob = default(NativeMethods.DATA_BLOB);
        try
        {
            Marshal.Copy(input, 0, inBlob.pbData, input.Length);
            bool ok = protect
                ? NativeMethods.CryptProtectData(ref inBlob, "ScreenQA", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    NativeMethods.CRYPTPROTECT_UI_FORBIDDEN, out outBlob)
                : NativeMethods.CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    NativeMethods.CRYPTPROTECT_UI_FORBIDDEN, out outBlob);
            if (!ok) throw new InvalidOperationException($"DPAPI error {Marshal.GetLastWin32Error()}");

            var result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        finally
        {
            // Затираем неуправляемые буферы перед освобождением.
            for (int i = 0; i < inBlob.cbData; i++) Marshal.WriteByte(inBlob.pbData, i, 0);
            Marshal.FreeHGlobal(inBlob.pbData);
            if (outBlob.pbData != IntPtr.Zero)
            {
                for (int i = 0; i < outBlob.cbData; i++) Marshal.WriteByte(outBlob.pbData, i, 0);
                NativeMethods.LocalFree(outBlob.pbData);
            }
        }
    }
}
