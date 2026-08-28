// 屏幕边缘极光流光：域扭曲 fbm 噪声产生流体涌动 + 距离场波浪边界。
// 颜色为 HSV 极光体系：色相随噪声/时间缓慢流转（青→蓝→紫的彩带感），
// 三层光结构（外扩柔光晕 + 主光带 + 边缘高光线）。
// 加法混合自发光；参数由 C# 按状态注入。
Shader "VoiceAI/LiquidEdgeGlow"
{
    Properties
    {
        _Hue    ("基础色相(0-1)", Float) = 0.56
        _Sat    ("饱和度", Float) = 0.55
        _Speed  ("流速", Float) = 0.05
        _Warp   ("液体扰动强度", Float) = 0.3
        _EdgePx ("边缘宽度(像素)", Float) = 110
        _Wave   ("行波强度", Float) = 0
        _Level  ("语音能量", Float) = 0
        _Alpha  ("整体强度", Float) = 0.75
        _EdgeWarp   ("边界波浪幅度(像素)", Float) = 30
        _EdgeWarpSpd("边界波浪变化速度", Float) = 0.15
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" }
        Blend SrcAlpha One          // 加法混合：光晕叠加发光
        Cull Off ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float _Hue, _Sat;
            float _Speed, _Warp, _EdgePx, _Wave, _Level, _Alpha;
            float _EdgeWarp, _EdgeWarpSpd;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f     { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            // ---- HSV 工具 ----
            float3 hsv2rgb(float3 c)
            {
                float3 rgb = clamp(abs(fmod(c.x * 6.0 + float3(0, 4, 2), 6.0) - 3.0) - 1.0, 0, 1);
                return c.z * lerp(float3(1, 1, 1), rgb, c.y);
            }

            float hash(float2 p) { return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }

            float noise(float2 p)
            {
                float2 i = floor(p), f = frac(p);
                // 五次插值（Perlin quintic）：一阶/二阶导数连续，无折角
                f = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
                float a = hash(i);
                float b = hash(i + float2(1, 0));
                float c = hash(i + float2(0, 1));
                float d = hash(i + float2(1, 1));
                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            /// 低频光滑 fbm：基频减半 + 八度更快衰减（3 octave），
            /// 波浪宽而圆润，高频毛刺被滤掉
            float fbmSmooth(float2 p)
            {
                p *= 0.5;
                float v = 0.0, a = 0.55;
                [unroll]
                for (int k = 0; k < 3; k++)
                {
                    v += a * noise(p);
                    p = p * 2.11 + float2(3.7, 1.1);
                    a *= 0.32;
                }
                return v;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 uv = i.uv;
                float2 res = _ScreenParams.xy;

                // 各向同性坐标（补偿屏幕宽高比，噪声不拉伸）
                float2 p = float2(uv.x * res.x / res.y, uv.y) * 3.0;
                float t = _Time.y * _Speed;

                // ---- 液态域扭曲（double fbm warp，流体般的涌动）----
                float2 q = float2(fbmSmooth(p + float2(0.0, 0.15 * t)),
                                  fbmSmooth(p + float2(5.2, 1.3) - 0.12 * t));
                float w = 0.6 + 1.8 * _Warp;
                float2 r = float2(fbmSmooth(p + w * q + float2(1.7, 9.2) + 0.35 * t),
                                  fbmSmooth(p + w * q + float2(8.3, 2.8) + 0.28 * t));
                float f = fbmSmooth(p + w * r + float2(0.5 * t, -0.3 * t));

                // ---- 边缘距离场（像素），低频噪声扭曲 → 不规则波浪边界 ----
                float t2 = _Time.y * _EdgeWarpSpd;
                float2 q2 = float2(fbmSmooth(p + float2(11.3, 3.1) + 0.2 * t2),
                                   fbmSmooth(p + float2(4.7, 7.9) - 0.15 * t2));
                float n2 = fbmSmooth(p + 1.2 * q2 + float2(0.1 * t2, 0.25 * t2));

                float2 px = uv * res;
                float d = min(min(px.x, res.x - px.x), min(px.y, res.y - px.y));
                d -= _EdgeWarp * (n2 * 2.0 - 1.0);
                float aa = fwidth(d) * 1.5;                    // 像素级抗锯齿
                float band     = 1.0 - smoothstep(0.0, _EdgePx, d + aa);         // 主光带（细）
                float haloBand = 1.0 - smoothstep(0.0, _EdgePx * 2.8, d + aa*2); // 光晕（宽得多）

                // ---- 三层光结构：细亮边线 + 窄主光带 + 宽广外扩光晕 ----
                // 亮度基线高、噪声只做小幅起伏（0.85~1.0）：光带任何位置都连续不断线
                float halo    = pow(haloBand, 0.60) * 0.50;                     // 外扩光晕：宽而柔
                float body    = pow(band, 2.0) * (0.62 + 0.38 * f);             // 主光带：细，噪声小幅起伏
                float edgeLn  = pow(band, 6.0) * (0.88 + 0.12 * f);             // 边缘高光线：细亮，近恒亮

                // ---- 沿边缘行进的波浪（思考态/波澜）----
                // 0.5+0.5*sin 映射到 [0,1]：波浪只增亮（波峰更亮），不压暗 → 不产生暗段断线
                float wave = 1.0 + _Wave * (0.5 + 0.5 * sin(9.42 * uv.x + 6.28 * uv.y + _Time.y * _Speed * 14.0));

                // ---- 彩虹色：色相沿屏幕边缘绕一整圈色环（红→橙→黄→绿→青→蓝→紫→红）----
                // 屏幕中心角度直接映射色相：边缘绕一圈正好走完全光谱，
                // 再叠加流光扰动（边界颜色互相渗透）与整体缓慢旋转
                float2 c = uv - 0.5; c.x *= res.x / res.y;
                float ang = atan2(c.y, c.x) / 6.28318 + 0.5;   // 0..1 绕屏幕一周
                float hue = frac(ang + 0.05 * _Time.y + 0.18 * (f - 0.5) + _Hue);
                float sat = _Sat * (0.92 + 0.08 * f);
                float val = halo + body + edgeLn;
                val *= wave;
                val *= 0.55 + 1.15 * _Level;   // 语音能量增强亮度
                val *= _Alpha;

                float3 rgb = hsv2rgb(float3(hue, sat, clamp(val, 0.0, 1.0)));
                float a = smoothstep(-fwidth(val), fwidth(val), val); // 亮度 AA，无台阶
                return fixed4(rgb, clamp(a, 0.0, 1.0));
            }
            ENDCG
        }
    }
}
