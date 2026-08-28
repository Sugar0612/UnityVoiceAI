using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

namespace VoiceAI
{
    /// <summary>
    /// 屏幕边缘液态流光（小爱同学式）：全屏 RawImage + LiquidEdgeGlow shader。
    /// 域扭曲 fbm 噪声产生流体涌动，边缘距离场形成光环，加法混合发光。
    /// 状态（与 VoiceAIState 对应）：
    /// 0=待机：宽而缓慢的平静流光；1=监听：波澜随说话音量涌动；
    /// 2=思考：沿边缘行进的循环波浪；3=回答：亮度随朗读能量起伏。
    /// 对外 API 与旧版一致（AttachToCanvas/SetState/SetSpeechLevel/SetVisible）。
    /// 仅作视觉呈现，不影响语音链路。
    /// </summary>
    public class EdgeGlowEffect : MonoBehaviour
    {
        private enum GlowState { Idle = 0, Listening = 1, Thinking = 2, Speaking = 3 }

        private struct StateParams
        {
            public float hue;      // 基础色相（0-1 HSV）
            public float sat;      // 饱和度
            public float speed;    // 噪声流速
            public float warp;     // 液体扰动强度
            public float edgePx;   // 边缘光环宽度（像素）
            public float wave;     // 行波强度
            public float alpha;    // 整体强度
            public float edgeWarp;    // 边界波浪幅度（像素，边界不规则凹凸）
            public float edgeWarpSpd; // 边界形状变化速度
        }

        // ---------- 各状态参数（极光色板：低饱和、色相区分状态）----------
        private static StateParams P(GlowState s)
        {
            switch (s)
            {
                case GlowState.Listening: // 彩虹 · 波澜随声音涌动
                    return new StateParams
                    {
                        hue = 0.06f, sat = 0.68f,
                        speed = 0.22f, warp = 1.00f, edgePx = 16f, wave = 0.18f, alpha = 1.00f,
                        edgeWarp = 95f, edgeWarpSpd = 0.55f
                    };
                case GlowState.Thinking: // 彩虹 · 循环行波
                    return new StateParams
                    {
                        hue = 0.11f, sat = 0.66f,
                        speed = 0.11f, warp = 0.55f, edgePx = 18f, wave = 0.42f, alpha = 0.90f,
                        edgeWarp = 60f, edgeWarpSpd = 0.30f
                    };
                case GlowState.Speaking: // 彩虹 · 随朗读能量呼吸
                    return new StateParams
                    {
                        hue = 0.44f, sat = 0.64f,
                        speed = 0.16f, warp = 0.85f, edgePx = 22f, wave = 0.10f, alpha = 1.00f,
                        edgeWarp = 70f, edgeWarpSpd = 0.40f
                    };
                default: // Idle：彩虹 · 宽缓平静
                    return new StateParams
                    {
                        hue = 0.56f, sat = 0.62f,
                        speed = 0.05f, warp = 0.30f, edgePx = 28f, wave = 0.00f, alpha = 0.75f,
                        edgeWarp = 42f, edgeWarpSpd = 0.12f
                    };
            }
        }

        private RawImage _img;
        private Material _mat;
        private Texture2D _white;   // RawImage 占位纹理（shader 不采样）
        private bool _visible;
        private GlowState _state = GlowState.Idle;

        private StateParams _cur, _target;
        private bool _inited;

        private float _externalLevel;   // 外部注入的语音能量 0..1
        private float _levelExpires;
        private float _levelCur;        // 平滑后的能量值（喂给 shader）

        public static EdgeGlowEffect AttachToCanvas(Canvas canvas)
        {
            if (canvas == null) return null;

            var existing = canvas.GetComponentInChildren<EdgeGlowEffect>(true);
            if (existing != null) return existing;

            var go = new GameObject("EdgeGlow", typeof(EdgeGlowEffect));
            go.transform.SetParent(canvas.transform, false);
            return go.GetComponent<EdgeGlowEffect>();
        }

        private void Start()
        {
            if (GetComponentInParent<Canvas>() == null)
            {
                Debug.LogWarning("[VoiceAI] 边缘光效需要挂在 Canvas 下");
                return;
            }

            var rt = GetComponent<RectTransform>();
            if (rt == null) rt = gameObject.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            // shader 放在 Resources 下保证随包构建（Shader.Find 在真机上不保证可用）
            var shader = Resources.Load<Shader>("Shaders/LiquidEdgeGlow");
            if (shader == null || !shader.isSupported)
            {
                Debug.LogWarning("[VoiceAI] 液态流光 shader 不可用，特效停用");
                return;
            }

            _mat = new Material(shader);
            _white = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            _img = gameObject.AddComponent<RawImage>();
            _img.material = _mat;
            _img.texture = _white;
            _img.raycastTarget = false;

            _cur = _target = P(GlowState.Idle);
            PushParams(_cur);
            _inited = true;
            SetVisible(true);

            // URP 项目：光晕的"发光感"来自 Bloom 后处理，缺失时自动配置
            EnsureBloom();
        }

        /// <summary>
        /// 确保 URP 后处理可用：相机开启 renderPostProcessing，
        /// 场景无全局 Bloom 时自动创建一个（threshold 0.6 / intensity 1.2）。
        /// 已有 Bloom 配置则不干预。失败只降级（无 Bloom 时光晕弱一些，其余正常）。
        /// </summary>
        private void EnsureBloom()
        {
            try
            {
                var cam = Camera.main;
                if (cam == null) return;
                var camData = cam.GetUniversalAdditionalCameraData();
                if (camData != null) camData.renderPostProcessing = true;

                // 场景已有全局 Bloom 就不重复添加
                var volumes = FindObjectsByType<Volume>(FindObjectsSortMode.None);
                foreach (var vol in volumes)
                {
                    if (vol.isGlobal && vol.sharedProfile != null &&
                        vol.sharedProfile.TryGet<Bloom>(out _))
                    {
                        Debug.Log("[VoiceAI] 检测到已有 Bloom，沿用场景配置");
                        return;
                    }
                }

                var go = new GameObject("EdgeGlowBloomVolume");
                var volume = go.AddComponent<Volume>();
                volume.isGlobal = true;
                var profile = ScriptableObject.CreateInstance<VolumeProfile>();
                var bloom = profile.Add<Bloom>(true);
                bloom.threshold.Override(0.60f);   // 只让高亮的边缘光带起晕
                bloom.intensity.Override(1.20f);
                bloom.scatter.Override(0.75f);     // 晕扩散得开
                volume.sharedProfile = profile;
                Debug.Log("[VoiceAI] 已自动创建 URP Bloom（光晕增强）");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VoiceAI] Bloom 配置失败（光晕减弱，其余正常）: " + e.Message);
            }
        }

        private void OnDestroy()
        {
            if (_mat != null) Destroy(_mat);
            if (_white != null) Destroy(_white);
        }

        /// <summary>state 对应 VoiceAIState：0=待机 1=监听 2=思考 3=回答</summary>
        public void SetState(int state)
        {
            _state = (GlowState)Mathf.Clamp(state, 0, 3);
            _target = P(_state);
        }

        /// <summary>
        /// 注入实时语音能量（0..1）。监听态喂用户说话音量（RMS），
        /// 回答态喂 TTS 播放音量；0.25 秒内无新数据回落到内置模拟。
        /// </summary>
        public void SetSpeechLevel(float level)
        {
            _externalLevel = Mathf.Clamp01(level);
            _levelExpires = Time.unscaledTime + 0.25f;
        }

        public void SetVisible(bool visible)
        {
            _visible = visible;
            if (_img != null) _img.enabled = visible;
        }

        private void Update()
        {
            if (!_inited || !_visible) return;

            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            float t = Time.unscaledTime;

            // 状态参数平滑过渡（色相走最短弧、其余线性）
            float k = dt * 4f;
            _cur.hue   = LerpHue(_cur.hue, _target.hue, k);
            _cur.sat   = Mathf.Lerp(_cur.sat, _target.sat, k);
            _cur.speed  = Mathf.Lerp(_cur.speed, _target.speed, k);
            _cur.warp   = Mathf.Lerp(_cur.warp, _target.warp, k);
            _cur.edgePx = Mathf.Lerp(_cur.edgePx, _target.edgePx, k);
            _cur.wave   = Mathf.Lerp(_cur.wave, _target.wave, k);
            _cur.alpha  = Mathf.Lerp(_cur.alpha, _target.alpha, k);
            _cur.edgeWarp    = Mathf.Lerp(_cur.edgeWarp, _target.edgeWarp, k);
            _cur.edgeWarpSpd = Mathf.Lerp(_cur.edgeWarpSpd, _target.edgeWarpSpd, k);

            // 语音能量：外部注入优先，过期回落（回答态用音节模拟）
            float levelTarget = t < _levelExpires ? _externalLevel : SimulatedSpeech(t);
            _levelCur = Mathf.Lerp(_levelCur, levelTarget, dt * (levelTarget > _levelCur ? 14f : 5f));

            _mat.SetFloat("_Hue", _cur.hue);
            _mat.SetFloat("_Sat", _cur.sat);
            _mat.SetFloat("_Speed", _cur.speed);
            _mat.SetFloat("_Warp", _cur.warp);
            _mat.SetFloat("_EdgePx", _cur.edgePx);
            _mat.SetFloat("_Wave", _cur.wave);
            _mat.SetFloat("_Alpha", _cur.alpha);
            _mat.SetFloat("_Level", _levelCur);
            _mat.SetFloat("_EdgeWarp", _cur.edgeWarp);
            _mat.SetFloat("_EdgeWarpSpd", _cur.edgeWarpSpd);
        }

        private void PushParams(StateParams p)
        {
            if (_mat == null) return;
            _mat.SetFloat("_Hue", p.hue);
            _mat.SetFloat("_Sat", p.sat);
            _mat.SetFloat("_Speed", p.speed);
            _mat.SetFloat("_Warp", p.warp);
            _mat.SetFloat("_EdgePx", p.edgePx);
            _mat.SetFloat("_Wave", p.wave);
            _mat.SetFloat("_Alpha", p.alpha);
            _mat.SetFloat("_Level", 0f);
            _mat.SetFloat("_EdgeWarp", p.edgeWarp);
            _mat.SetFloat("_EdgeWarpSpd", p.edgeWarpSpd);
        }

        /// <summary>回答态的音量模拟（系统TTS无法采样真实音量）：按音节节律起伏</summary>
        private float SimulatedSpeech(float t)
        {
            if (_state != GlowState.Speaking) return 0f;
            float phase = t * 3.3f;             // ~3.3 个音节/秒
            int idx = Mathf.FloorToInt(phase);
            float frac = phase - idx;
            float amp = 0.45f + 0.55f * Hash01(idx);                  // 每音节响度不同
            float env = Mathf.Pow(Mathf.Sin(Mathf.PI * frac), 1.5f);  // 起-落包络
            return amp * env;
        }

        /// <summary>色相插值（走最短弧，避免 0.99→0.01 时绕远变灰）</summary>
        private static float LerpHue(float a, float b, float k)
        {
            float diff = b - a;
            if (diff > 0.5f) diff -= 1f;
            else if (diff < -0.5f) diff += 1f;
            return Mathf.Repeat(a + diff * k, 1f);
        }

        private static float Hash01(int n)
        {
            float h = Mathf.Sin(n * 127.1f) * 43758.5453f;
            return h - Mathf.Floor(h);
        }
    }
}
