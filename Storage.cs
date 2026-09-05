using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ForumReviewMonitor;

public static class Storage
{
    public static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "data");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public static string? LoadWarning { get; private set; }
    public static Settings LoadSettings()
    {
        Directory.CreateDirectory(DirectoryPath);
        string path = Path.Combine(DirectoryPath, "settings.dat");
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<Settings>(Dpapi.Transform(File.ReadAllBytes(path), false), Options) ?? throw new InvalidDataException(); }
        catch { LoadWarning = "配置无法解密或已损坏，已加载默认值。原配置尚未覆盖；跨电脑或 Windows 账号迁移后需重新填写凭据。"; return new(); }
    }
    public static void SaveSettings(Settings settings) => AtomicWrite(Path.Combine(DirectoryPath, "settings.dat"), Dpapi.Transform(JsonSerializer.SerializeToUtf8Bytes(settings, Options), true));
    private static string StatePath(Settings settings)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settings.BaseUri.AbsoluteUri)))[..16];
        return Path.Combine(DirectoryPath, $"state-{key}.json");
    }
    public static DeliveryState LoadState(Settings settings)
    {
        var path = StatePath(settings);
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<DeliveryState>(File.ReadAllText(path)) ?? throw new InvalidDataException(); }
        catch { throw new InvalidOperationException("去重记录损坏。请备份后移走 data 下的 state 文件，再重试；重建后会重新提醒当前待审项。"); }
    }
    public static void SaveState(Settings settings, DeliveryState state) => AtomicWrite(StatePath(settings), JsonSerializer.SerializeToUtf8Bytes(state, Options));
    public static void AtomicWrite(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllBytes(temporary, content);
        File.Move(temporary, path, true);
    }
}

internal static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
    public static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            bool success = protect ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new CryptographicException("Windows 凭据加密／解密失败。");
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, output.Length);
            return result;
        }
        finally { Marshal.FreeHGlobal(input.Data); if (output.Data != IntPtr.Zero) LocalFree(output.Data); }
    }
}

public sealed class AppLog
{
    public event Action<string>? Added;
    private readonly Queue<string> lines = new();
    public IEnumerable<string> Lines => lines.ToArray();
    public void Write(string level, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
        try
        {
            string directory = Path.Combine(Storage.DirectoryPath, "logs");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, DateTime.Now.ToString("yyyy-MM-dd") + ".log");
            if (File.Exists(path) && new FileInfo(path).Length > 5 * 1024 * 1024)
                File.Move(path, Path.Combine(directory, DateTime.Now.ToString("yyyy-MM-dd-HHmmss-ffff") + ".log"));
            File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            foreach (string old in Directory.GetFiles(directory, "*.log").Where(x => File.GetLastWriteTime(x) < DateTime.Now.AddDays(-30))) File.Delete(old);
        }
        catch { line += " [日志文件写入失败，请检查目录权限和磁盘空间]"; }
        lines.Enqueue(line);
        while (lines.Count > 2000) lines.Dequeue();
        Added?.Invoke(line);
    }
}

