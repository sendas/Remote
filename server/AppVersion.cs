using System.Reflection;

namespace ClassroomServer;

/// <summary>Versão e data de compilação deste servidor (mostradas no topo da consola).</summary>
public static class AppVersion
{
    private static readonly Assembly Asm = typeof(AppVersion).Assembly;

    /// <summary>Ex.: "2.1.0" (sem o identificador do commit).</summary>
    public static string Version =>
        (Asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0").Split('+')[0];

    /// <summary>Identificador curto do commit no GitHub, se a compilação veio do GitHub.</summary>
    public static string Commit
    {
        get
        {
            var v = Asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
            var i = v.IndexOf('+');
            if (i <= 0) return "";
            var c = v[(i + 1)..];
            return c.Length > 7 ? c[..7] : c;
        }
    }

    public static DateTimeOffset? BuildDate =>
        DateTimeOffset.TryParse(Asm.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildDate")?.Value, out var d) ? d : null;

    /// <summary>Quando este servidor começou a correr (= última atualização/reinício).</summary>
    public static readonly DateTimeOffset StartedAt = DateTimeOffset.Now;
}
