using System.Text;

namespace NodeGroups.Storage;

/// <summary>使用同目录临时文件原子替换侧车文件；从不写入原生剧情文件。</summary>
internal sealed class PhysicalGroupFileSystem : IGroupFileSystem
{
    // 编解码器仅调用无状态的 GetString/GetBytes；不共享跨次读取的 Decoder/Encoder 状态。
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    /// <inheritdoc />
    public bool FileExists(string path) => File.Exists(path);

    /// <inheritdoc />
    public string ReadText(string path, int maxBytes)
    {
        // 同一文件句柄贯穿长度检查和读取，避免检查与读取落到不同文件上。
        // 循环仍检查累计字节数，保证文件在读取期间增长时也不会越过上限。
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var initialLength = stream.Length;
        if (initialLength > maxBytes) throw new GroupValidationException("FileTooLarge", "The group file exceeds the permitted byte limit. It was left untouched.");
        using var bytes = new MemoryStream((int)initialLength);
        var buffer = new byte[4096];
        int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (bytes.Length + count > maxBytes) throw new GroupValidationException("FileTooLarge", "The group file exceeds the permitted byte limit. It was left untouched.");
            bytes.Write(buffer, 0, count);
        }
        string contents;
        // 只解码缓冲区中已写入的部分，省去 ToArray 对完整文件的一次额外分配与复制。
        try { contents = StrictUtf8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length); }
        catch (DecoderFallbackException)
        {
            throw new GroupValidationException("InvalidJson", "The group file is not valid UTF-8 JSON. It was left untouched for manual recovery.");
        }
        // 兼容外部工具写入的 UTF-8 BOM；非法 UTF-8 仍由严格解码拒绝，保留原文件。
        return contents.Length > 0 && contents[0] == '\uFEFF' ? contents[1..] : contents;
    }

    /// <inheritdoc />
    public void WriteNewText(string path, string text)
    {
        var bytes = StrictUtf8.GetBytes(text);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(flushToDisk: true);
    }

    /// <inheritdoc />
    public void Replace(string temporaryPath, string destinationPath)
    {
        if (File.Exists(destinationPath))
            File.Replace(temporaryPath, destinationPath, destinationBackupFileName: null);
        else
            File.Move(temporaryPath, destinationPath); // 目标被并发创建时抛错，不覆盖对方已写入的文件。
    }

    /// <inheritdoc />
    public void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
