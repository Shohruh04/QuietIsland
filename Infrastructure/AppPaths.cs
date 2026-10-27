using System;
using System.IO;

namespace QuietIsland;

internal static class AppPaths
{
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuietIsland");
}
