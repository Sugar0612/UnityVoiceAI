using System;
using UnityEditor;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using VoiceAI;

namespace VoiceAI.EditorTools
{
    /// <summary>
    /// 一键生成语音 AI 演示 UI（Canvas + 按钮 + 三行文字 + 总控组件）。
    /// 菜单：Tools → VoiceAI → 创建演示 UI
    /// </summary>
    public static class VoiceAISetup
    {
        [MenuItem("Tools/VoiceAI/创建演示 UI")]
        public static void CreateDemoUI()
        {
            if (UnityEngine.Object.FindFirstObjectByType<VoiceAIController>() != null)
            {
                Debug.LogWarning("[VoiceAI] 场景中已存在 VoiceAIController，请勿重复创建。");
                return;
            }

            // ---------- Canvas ----------
            var canvasGO = new GameObject("VoiceAI_Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasGO.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            var scaler = canvasGO.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080, 1920);

            var controller = canvasGO.AddComponent<VoiceAIController>();

            // ---------- 录音按钮 ----------
            var btnGO = new GameObject("RecordButton", typeof(RectTransform), typeof(Image), typeof(Button));
            btnGO.transform.SetParent(canvasGO.transform, false);
            var btnRect = btnGO.GetComponent<RectTransform>();
            btnRect.anchorMin = new Vector2(0.5f, 0.30f);
            btnRect.anchorMax = new Vector2(0.5f, 0.30f);
            btnRect.pivot = new Vector2(0.5f, 0.5f);
            btnRect.sizeDelta = new Vector2(360, 130);

            var btnImage = btnGO.GetComponent<Image>();
            btnImage.color = new Color(0.16f, 0.55f, 0.95f, 1f);

            var button = btnGO.GetComponent<Button>();
            button.targetGraphic = btnImage;
            // 持久化监听：会写进场景文件，保存/重开/打包后仍然有效
            // （普通 AddListener 是运行时委托，序列化时会丢失）
            UnityEventTools.AddPersistentListener(button.onClick, controller.ToggleListening);
            // 总控组件自身也实现了 IPointerClickHandler，即使不绑定也能响应点击

            CreateText(btnGO.transform, "Label", "手动说话",
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                Vector2.zero, new Vector2(340, 80), 48, TextAnchor.MiddleCenter, Color.white);

            // ---------- 文字区 ----------
            var status = CreateText(canvasGO.transform, "StatusText", "说\"何夕月\"唤醒我",
                new Vector2(0.5f, 0.85f), new Vector2(0.5f, 0.85f),
                Vector2.zero, new Vector2(960, 60), 32, TextAnchor.MiddleCenter, Color.white);

            var recognized = CreateText(canvasGO.transform, "RecognizedText", "",
                new Vector2(0.5f, 0.72f), new Vector2(0.5f, 0.72f),
                Vector2.zero, new Vector2(960, 120), 36, TextAnchor.MiddleCenter, new Color(1f, 1f, 1f, 0.9f));

            var reply = CreateText(canvasGO.transform, "ReplyText", "",
                new Vector2(0.5f, 0.55f), new Vector2(0.5f, 0.55f),
                Vector2.zero, new Vector2(960, 240), 38, TextAnchor.MiddleCenter, new Color(0.6f, 1f, 0.7f, 1f));

            // 通过 SerializedObject 关联控制器上的私有序列化字段
            var so = new SerializedObject(controller);
            so.FindProperty("statusText").objectReferenceValue = status;
            so.FindProperty("recognizedText").objectReferenceValue = recognized;
            so.FindProperty("replyText").objectReferenceValue = reply;
            so.ApplyModifiedProperties();

            // ---------- EventSystem ----------
            if (UnityEngine.Object.FindFirstObjectByType<EventSystem>() == null)
            {
                var esGO = new GameObject("EventSystem", typeof(EventSystem));
                var inputModuleType = Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
                if (inputModuleType != null)
                    esGO.AddComponent(inputModuleType);
                else
                    esGO.AddComponent<StandaloneInputModule>();
            }

            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                UnityEngine.SceneManagement.SceneManager.GetActiveScene());

            Selection.activeGameObject = canvasGO;
            Debug.Log("[VoiceAI] 演示 UI 已创建。唤醒词模式默认开启（说\"何夕月\"自动开始录音，按钮为手动备用）；选中 VoiceAI_Canvas 填入各 API Key 后构建到 Android 真机测试。");
        }

        private static Text CreateText(Transform parent, string name, string content,
            Vector2 anchorMin, Vector2 anchorMax, Vector2 anchoredPos, Vector2 size,
            int fontSize, TextAnchor alignment, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);

            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = anchoredPos;
            rect.sizeDelta = size;

            var text = go.GetComponent<Text>();
            text.text = content;
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.color = color;
            text.raycastTarget = false; // 不挡按钮点击
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow; // 长文字不截断
            return text;
        }
    }
}
