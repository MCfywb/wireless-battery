namespace PeripheralBattery.Protocol.Tests;

/// <summary>从插件夹具目录加载十六进制样本。</summary>
internal static class FixtureLoader
{
    /// <summary>夹具目录（构建时复制到输出目录）。</summary>
    public static string Directory { get; } = Path.Combine(AppContext.BaseDirectory, "fixtures");

    /// <summary>读取夹具并解析为字节数组；忽略以 # 开头的注释行。</summary>
    public static byte[] Load(string fileName)
    {
        var path = Path.Combine(Directory, fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"未找到夹具：{path}", path);
        }

        var lines = File.ReadAllLines(path)
            .Where(l => !l.TrimStart().StartsWith("#", StringComparison.Ordinal))
            .Where(l => !string.IsNullOrWhiteSpace(l));

        var hex = string.Concat(lines.Select(l => l.Replace(" ", string.Empty, StringComparison.Ordinal)));
        return Convert.FromHexString(hex);
    }
}
