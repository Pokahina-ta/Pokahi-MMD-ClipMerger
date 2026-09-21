using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

// Editor-only: additional VMD conversions often contain a default body pose.
// Copy only facial blendshape curves from them, preserving the main humanoid motion.
namespace Pokahi.MmdClipMerger
{
public static class ClipMerger
{
    public static bool IsFace(EditorCurveBinding b)
    { return b.type == typeof(SkinnedMeshRenderer) && b.propertyName.StartsWith("blendShape.", StringComparison.Ordinal); }
    public static string Key(EditorCurveBinding b)
    { return b.path + "|" + b.type.FullName + "|" + b.propertyName; }
    public static int FaceCount(AnimationClip clip)
    { return clip == null ? 0 : AnimationUtility.GetCurveBindings(clip).Count(IsFace); }
    public static AnimationClip Merge(AnimationClip body, AnimationClip lip, AnimationClip face, bool lipWins, string output)
    {
        if (body == null || (lip == null && face == null)) throw new ArgumentException("身体と、リップまたは表情を指定してください。");
        if (lip == body || face == body) throw new ArgumentException("身体と追加ファイルには別のアニメーションを指定してください。");
        if (lip != null && FaceCount(lip) == 0 || face != null && FaceCount(face) == 0)
            throw new ArgumentException("追加ファイルにブレンドシェイプがありません。VMD変換時の表情の対応付けを確認してください。");
        if (string.IsNullOrWhiteSpace(output)) throw new ArgumentException("保存先を指定してください。");
        output = output.Replace('\\', '/');
        if (!output.StartsWith("Assets/", StringComparison.Ordinal) || !output.EndsWith(".anim", StringComparison.OrdinalIgnoreCase) || output.Split('/').Contains(".."))
            throw new ArgumentException("Assets内の.animを保存先にしてください。");
        if (!AssetDatabase.IsValidFolder(Path.GetDirectoryName(output).Replace('\\', '/'))) throw new ArgumentException("保存先フォルダーがありません。");
        output = AssetDatabase.GenerateUniqueAssetPath(output);
        var merged = Object.Instantiate(body);
        try
        {
            merged.name = Path.GetFileNameWithoutExtension(output);
            var bindings = new Dictionary<string, EditorCurveBinding>();
            var curves = new Dictionary<string, AnimationCurve>();
            foreach (var clip in lipWins ? new[] { face, lip } : new[] { lip, face })
            {
                if (clip == null) continue;
                foreach (var b in AnimationUtility.GetCurveBindings(clip).Where(IsFace))
                { string key = Key(b); bindings[key] = b; curves[key] = AnimationUtility.GetEditorCurve(clip, b); }
            }
            var keys = bindings.Keys.ToArray();
            AnimationUtility.SetEditorCurves(merged, keys.Select(k => bindings[k]).ToArray(), keys.Select(k => curves[k]).ToArray());
            var settings = AnimationUtility.GetAnimationClipSettings(body);
            settings.stopTime = Mathf.Max(body.length, Mathf.Max(lip == null ? 0 : lip.length, face == null ? 0 : face.length));
            AnimationUtility.SetAnimationClipSettings(merged, settings);
            merged.frameRate = body.frameRate;
            AssetDatabase.CreateAsset(merged, output);
            AssetDatabase.SaveAssets();
            return merged;
        }
        catch { if (!AssetDatabase.Contains(merged)) Object.DestroyImmediate(merged); throw; }
    }
}

public class ClipMergerWindow : EditorWindow
{
    AnimationClip body, lip, face;
    bool lipWins = true;
    string outputName = "", message = "";
    AnimationClip result;
    Vector2 scroll;
    [MenuItem("Tools/Pokahi MMD Clip Merger/モーション・リップ・表情を結合")]
    public static void Open() { GetWindow<ClipMergerWindow>("アニメーション結合").minSize = new Vector2(470, 490); }
    [MenuItem("Assets/Pokahi MMD Clip Merger/選択したアニメーションを結合", false, 2000)]
    static void FromSelection() { Open(); GetWindow<ClipMergerWindow>().AssignSelection(); }
    void AssignSelection()
    {
        body = lip = face = null;
        var unknown = new List<AnimationClip>();
        foreach (var clip in Selection.objects.OfType<AnimationClip>())
        {
            var n = clip.name.ToLowerInvariant();
            if (n.Contains("lip") || n.Contains("リップ")) { if (lip == null) lip = clip; else unknown.Add(clip); }
            else if (n.Contains("face") || n.Contains("eye") || n.Contains("表情") || n.Contains("目線")) { if (face == null) face = clip; else unknown.Add(clip); }
            else unknown.Add(clip);
        }
        if (unknown.Count == 1) body = unknown[0];
        outputName = body == null ? "" : body.name + "_Merged";
        message = "ファイル名から振り分けました。身体・リップ・表情の欄が正しいか確認してください。";
        if (unknown.Count > 1) message += " 判別できないファイルは手動で指定してください。";
    }
    void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);
        EditorGUILayout.LabelField("身体・リップ・表情を1本に", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("同じ曲・同じモデル用に変換したアニメーションを指定します。原本は残し、新しい .anim を作ります。", MessageType.Info);
        if (GUILayout.Button("Projectで選択したファイルを自動で振り分け")) AssignSelection();
        EditorGUI.BeginChangeCheck();
        body = (AnimationClip)EditorGUILayout.ObjectField("身体モーション（必須）", body, typeof(AnimationClip), false);
        if (EditorGUI.EndChangeCheck() && body != null) outputName = body.name + "_Merged";
        lip = (AnimationClip)EditorGUILayout.ObjectField("リップ（口）", lip, typeof(AnimationClip), false);
        face = (AnimationClip)EditorGUILayout.ObjectField("表情・目線", face, typeof(AnimationClip), false);
        EditorGUILayout.Space();
        if (lip != null && face != null)
        {
            var lipKeys = new HashSet<string>(AnimationUtility.GetCurveBindings(lip).Where(ClipMerger.IsFace).Select(ClipMerger.Key));
            int conflicts = AnimationUtility.GetCurveBindings(face).Count(b => ClipMerger.IsFace(b) && lipKeys.Contains(ClipMerger.Key(b)));
            if (conflicts > 0) EditorGUILayout.HelpBox("重複する表情カーブ：" + conflicts + " 本。優先側のカーブ全体で置き換えます。値が0のカーブも対象です。", MessageType.Warning);
        }
        lipWins = EditorGUILayout.Popup("同じ表情が重なる場合", lipWins ? 0 : 1, new[] { "リップを優先（通常はこちら）", "表情・目線を優先" }) == 0;
        EditorGUILayout.HelpBox("追加するのはブレンドシェイプのみです。身体の動き・向き・設定を保持します。眼球ボーンの動きは追加しません。再生速度や開始時刻は変更しません。", MessageType.None);
        Info("身体", body); Info("リップ", lip); Info("表情", face);
        if (body != null && ((lip != null && Mathf.Abs(lip.length - body.length) > 1) || (face != null && Mathf.Abs(face.length - body.length) > 1)))
            EditorGUILayout.HelpBox("ファイルの長さが異なります。長さは最も長いファイルに合わせます。音源とのタイミングは統合後に確認してください。", MessageType.Warning);
        outputName = EditorGUILayout.TextField("保存ファイル名", outputName);
        using (new EditorGUI.DisabledScope(body == null || (lip == null && face == null) || EditorApplication.isPlayingOrWillChangePlaymode))
        {
            if (GUILayout.Button("結合して保存…", GUILayout.Height(34)))
            {
                var folder = Path.GetDirectoryName(AssetDatabase.GetAssetPath(body)).Replace('\\', '/');
                if (!folder.StartsWith("Assets")) folder = "Assets";
                var name = string.IsNullOrWhiteSpace(outputName) ? body.name + "_Merged" : outputName;
                foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
                var path = EditorUtility.SaveFilePanelInProject("統合アニメーションを保存", name, "anim", "同名ファイルがある場合は別名で保存します。", folder);
                if (!string.IsNullOrEmpty(path))
                {
                    try { result = ClipMerger.Merge(body, lip, face, lipWins, path); message = "保存しました：" + AssetDatabase.GetAssetPath(result); EditorGUIUtility.PingObject(result); }
                    catch (Exception e) { message = "作成できませんでした：" + e.Message; }
                }
            }
        }
        if (!string.IsNullOrEmpty(message)) EditorGUILayout.HelpBox(message, MessageType.Info);
        if (result != null)
        {
            EditorGUILayout.ObjectField("統合結果", result, typeof(AnimationClip), false);
            EditorGUILayout.LabelField("統合結果を、Animatorや使用先の曲登録ツールへ指定してください。", EditorStyles.wordWrappedLabel);
        }
        EditorGUILayout.EndScrollView();
    }
    static void Info(string label, AnimationClip clip)
    { if (clip != null) EditorGUILayout.LabelField(label, clip.length.ToString("0.00") + " 秒 / " + clip.frameRate.ToString("0.#") + " fps / 表情 " + ClipMerger.FaceCount(clip) + " 本"); }
}

}
