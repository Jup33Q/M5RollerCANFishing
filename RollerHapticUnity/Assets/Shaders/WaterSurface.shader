// WaterSurface.shader — 钓鱼模拟器水面（Built-in 管线，无贴图纯程序化）
//
// 折射：GrabPass 屏幕采样 + 波动法线扭曲（_Distort）
// 法线：4 层不同方向/频率正弦波（解析导数，零额外采样）+ 可选 fbm 细节
// 菲涅尔：掠射角反射 _SkyTint；太阳高光 Blinn-Phong + 高频噪声闪鳞（glitter）
// 颜色：_ShallowColor/_DeepColor 随视角混合；Queue=Transparent，alpha:fade
//
// 性能：_Detail ∈ [0,1] 门控贵价部分——0 时只有 4 层正弦解析法线
// （编辑器非 Play 模式由 WaterQuality.cs 用 MaterialPropertyBlock 自动调低）。
//
// 注意：o.Normal 写的是世界空间向量直接当切空间用——仅当水面 Plane 不旋转时成立
// （Unity Plane 切空间轴与世界轴对齐：tangent=+X normal=+Y bitangent=+Z）。
Shader "Custom/WaterSurface"
{
    Properties
    {
        _ShallowColor      ("浅水色", Color) = (0.18, 0.50, 0.58, 1)
        _DeepColor         ("深水色", Color) = (0.02, 0.13, 0.26, 1)
        _SkyTint           ("天空反射色", Color) = (0.55, 0.74, 0.90, 1)
        _RefrTint          ("折射染色强度", Range(0, 1)) = 0.45
        _Distort           ("折射扭曲", Range(0, 0.2)) = 0.045
        _WaveScale         ("波纹缩放", Range(0.1, 20)) = 3.0
        _WaveSpeed         ("波纹速度", Range(0, 5)) = 0.7
        _WaveAmp           ("法线强度", Range(0, 3)) = 0.55
        _FresnelPower      ("菲涅尔指数", Range(0.5, 8)) = 3.0
        _SunColor          ("太阳高光色", Color) = (1.0, 0.96, 0.82, 1)
        _SunPower          ("太阳高光锐度", Range(2, 1024)) = 260
        _SunIntensity      ("太阳高光强度", Range(0, 8)) = 2.2
        _GlitterScale      ("闪鳞密度", Range(5, 200)) = 55
        _GlitterIntensity  ("闪鳞强度", Range(0, 6)) = 1.2
        _Alpha             ("整体不透明度", Range(0, 1)) = 0.92
        _Detail            ("细节等级(0=仅正弦)", Range(0, 1)) = 1
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }

        // 抓取水面之后的场景（鱼/浮漂是 Opaque 先渲，会被折射扭曲——正确的水面效果）
        GrabPass { "_WaterGrab" }

        CGPROGRAM
        #pragma surface surf Water alpha:fade
        #pragma target 3.0

        sampler2D _WaterGrab;

        half4 _ShallowColor, _DeepColor, _SkyTint, _SunColor;
        float _RefrTint, _Distort, _WaveScale, _WaveSpeed, _WaveAmp;
        float _FresnelPower, _SunPower, _SunIntensity;
        float _GlitterScale, _GlitterIntensity, _Alpha, _Detail;

        struct Input
        {
            float3 worldPos;
            float4 screenPos;
        };

        // ---- value noise（仅 _Detail > 0 时使用）----
        float hash21(float2 p)
        {
            p = frac(p * float2(234.34, 435.345));
            p += dot(p, p + 34.23);
            return frac(p.x * p.y);
        }
        float vnoise(float2 p)
        {
            float2 i = floor(p), f = frac(p);
            f = f * f * (3.0 - 2.0 * f);
            float a = hash21(i);
            float b = hash21(i + float2(1, 0));
            float c = hash21(i + float2(0, 1));
            float d = hash21(i + float2(1, 1));
            return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
        }
        // fbm 细节高度（2 octave）
        float DetailH(float2 p, float t)
        {
            float2 q = p * 1.9 + float2(t * 0.22, -t * 0.17);
            return (vnoise(q) * 0.65 + vnoise(q * 2.13 + 17.7) * 0.35 - 0.5) * 0.42;
        }

        // ---- 波动高度场：4 层正弦，解析导数（一次 sin+cos 同时出高度与梯度）----
        // 每层: dir(归一化方向) × freq + t×speed → amp×sin(phase)
        void WaveDG(float2 p, float t, out float h, out float2 grad)
        {
            float ph;
            h = 0.0; grad = float2(0.0, 0.0);

            ph = dot(p, float2(1.0, 0.0)) * 1.0 + t * 1.3;
            h += 0.34 * sin(ph);
            grad += 0.34 * 1.0 * cos(ph) * float2(1.0, 0.0);

            ph = dot(p, float2(0.66, 0.75)) * 1.7 - t * 1.05;
            h += 0.27 * sin(ph);
            grad += 0.27 * 1.7 * cos(ph) * float2(0.66, 0.75);

            ph = dot(p, float2(-0.44, 0.90)) * 2.4 + t * 0.85;
            h += 0.19 * sin(ph);
            grad += 0.19 * 2.4 * cos(ph) * float2(-0.44, 0.90);

            ph = dot(p, float2(0.94, -0.34)) * 3.6 + t * 1.6;
            h += 0.10 * sin(ph);
            grad += 0.10 * 3.6 * cos(ph) * float2(0.94, -0.34);

            // fbm 细节：前向差分 2 次采样，_Detail 加权（0 时整段跳过）
            if (_Detail > 0.001)
            {
                float e = 0.08;
                float d0 = DetailH(p, t);
                float dx = DetailH(p + float2(e, 0.0), t);
                float dz = DetailH(p + float2(0.0, e), t);
                h += d0 * _Detail;
                grad += (float2(dx - d0, dz - d0) / e) * _Detail;
            }
        }

        void surf(Input IN, inout SurfaceOutput o)
        {
            float t = _Time.y * _WaveSpeed;
            float2 p = IN.worldPos.xz * _WaveScale;

            float h; float2 grad;
            WaveDG(p, t, h, grad);
            float3 n = normalize(float3(-grad.x * _WaveAmp, 1.0, -grad.y * _WaveAmp));

            float3 viewDir = normalize(_WorldSpaceCameraPos - IN.worldPos);
            float ndv = saturate(dot(n, viewDir));
            float fres = pow(1.0 - ndv, _FresnelPower);

            // 折射：GrabPass + 法线扭曲
            float2 grabUV = IN.screenPos.xy / IN.screenPos.w;
            grabUV += n.xz * _Distort;
            half3 refr = tex2D(_WaterGrab, grabUV).rgb;

            // 折射染色：视角越陡越深水色
            half3 tint = lerp(_ShallowColor.rgb, _DeepColor.rgb, saturate(1.0 - ndv));
            refr *= lerp(half3(1, 1, 1), tint * 1.7, _RefrTint);

            // 菲涅尔：掠射角混入天空色
            half3 col = lerp(refr, _SkyTint.rgb, fres * 0.85);

            // 太阳高光：Blinn-Phong + 闪鳞（高频噪声，随 _Detail 衰减）
            float3 l = normalize(_WorldSpaceLightPos0.xyz);
            float3 hv = normalize(l + viewDir);
            float spec = pow(saturate(dot(n, hv)), _SunPower);
            float gl = 1.0;
            if (_Detail > 0.001 && _GlitterIntensity > 0.001)
            {
                gl = vnoise(p * (_GlitterScale / _WaveScale) + float2(t * 2.3, -t * 1.9));
                gl = lerp(1.0, pow(gl, 3.0), _Detail); // 稀疏亮点
            }
            col += _SunColor.rgb * spec * _SunIntensity * (0.35 + _GlitterIntensity * gl);

            o.Albedo = col;
            o.Normal = n;          // 世界轴=切空间轴（Plane 未旋转，见文件头注释）
            o.Alpha = saturate(_Alpha + fres * (1.0 - _Alpha));
        }

        // 颜色全部在 surf 里算完（GrabPass 折射与光照模型解耦）
        half4 LightingWater(SurfaceOutput s, half3 lightDir, half3 viewDir, half atten)
        {
            return half4(s.Albedo, s.Alpha);
        }
        ENDCG
    }

    Fallback Off
}
