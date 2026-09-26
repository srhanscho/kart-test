using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Writes the phone controller page to Logs/controller_page.html so it can be opened as a local
/// file with mock params (e.g. controller_page.html?mock=lobby&amp;leader=1&amp;debug=1).
/// Batch: -executeMethod ControllerPageExport.Write
/// </summary>
public static class ControllerPageExport
{
    public const string OutputPath = "Logs/controller_page.html";

    [MenuItem("Tools/Kart/Export Phone Controller Page")]
    public static void Write()
    {
        Directory.CreateDirectory("Logs");
        File.WriteAllText(OutputPath, ControllerPage.Html, new System.Text.UTF8Encoding(false));
        string full = Path.GetFullPath(OutputPath);
        Debug.Log($"[ControllerPageExport] wrote {full} ({ControllerPage.Html.Length} chars). Open e.g. file:///{full.Replace('\\', '/')}?mock=lobby&leader=1&debug=1");
    }
}
