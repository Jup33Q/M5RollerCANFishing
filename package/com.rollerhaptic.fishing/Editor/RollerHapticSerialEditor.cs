/*
 * RollerHapticSerialEditor.cs — RollerHapticSerial 的 Inspector 增强：
 * 「扫描串口」按钮列出 /dev/cu.usb* 设备，「使用」一键填入 Port Name，
 * Play 中可「重新扫描并连接」立即生效（不用重启 Play）。
 */
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(RollerHapticSerial))]
public class RollerHapticSerialEditor : Editor
{
    string[] found = new string[0];
    bool scanned;

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        var s = (RollerHapticSerial)target;

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("端口扫描", EditorStyles.boldLabel);

        if (GUILayout.Button("扫描串口"))
        {
            found = RollerHapticSerial.ScanPorts().ToArray();
            scanned = true;
        }

        if (scanned && found.Length == 0)
            EditorGUILayout.HelpBox("未发现 /dev/cu.usb* 设备，检查 USB 连接", MessageType.Warning);

        foreach (var p in found)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(p, GUILayout.MinWidth(170));
            using (new EditorGUI.DisabledScope(s.portName == p))
            {
                if (GUILayout.Button("使用", GUILayout.Width(50)))
                {
                    Undo.RecordObject(s, "Set Serial Port");
                    s.portName = p;
                    EditorUtility.SetDirty(s);
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        using (new EditorGUI.DisabledScope(!Application.isPlaying))
        {
            if (GUILayout.Button("重新扫描并连接（Play 中）"))
                s.Rescan();
        }
    }
}
