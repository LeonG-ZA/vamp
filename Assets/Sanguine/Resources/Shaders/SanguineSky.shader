// Night/day sky for Sanguine City (Built-in render pipeline).
// Samples two equirectangular HDR skies (Poly Haven, CC0), blends them by time of day,
// adds sodium light pollution near the horizon and fades into the scene fog.
Shader "Sanguine/Sky"
{
    Properties
    {
        [NoScaleOffset] _NightTex ("Night sky (HDR, lat-long)", 2D) = "black" {}
        [NoScaleOffset] _DayTex ("Day sky (HDR, lat-long)", 2D) = "black" {}
        _DayBlend ("Day blend", Range(0, 1)) = 0
        _NightExposure ("Night exposure", Float) = 1
        _DayExposure ("Day exposure", Float) = 1
        _Rotation ("Rotation (degrees)", Range(0, 360)) = 0
        _HorizonGlow ("City glow", Color) = (0.06, 0.035, 0.02, 1)
        _Clamp ("Max radiance (keeps moon/sun out of ambient)", Float) = 40
        _HorizonFog ("Fade into fog at horizon", Range(0, 1)) = 0.85
    }
    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" }
        Cull Off ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _NightTex, _DayTex;
            half4 _NightTex_HDR, _DayTex_HDR;
            half _DayBlend, _NightExposure, _DayExposure, _HorizonFog, _Clamp;
            float _Rotation;
            half4 _HorizonGlow;

            struct appdata
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 dir : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.dir = v.vertex.xyz;
                return o;
            }

            // Direction -> equirectangular UV. Must match ArtLibrary/Atmosphere (moon direction) on the C# side.
            float2 LatLong(float3 d)
            {
                d = normalize(d);
                float r = radians(_Rotation);
                float s = sin(r), c = cos(r);
                d = float3(d.x * c - d.z * s, d.y, d.x * s + d.z * c);
                float u = 0.5 + atan2(d.x, d.z) / (2.0 * UNITY_PI);
                float v = 0.5 + asin(clamp(d.y, -1.0, 1.0)) / UNITY_PI;
                return float2(u, v);
            }

            half3 SampleHdr(sampler2D tex, half4 decode, float2 uv)
            {
                half4 c = tex2Dlod(tex, float4(uv, 0, 0));
                // Poly Haven skies are exposure-normalised with an extremely bright moon/sun disc;
                // clamp it so the explicit moon/sun light, not the sky, provides the direct light.
                return min(decode.x > 0 ? DecodeHDR(c, decode) : c.rgb, _Clamp);
            }

            half4 frag(v2f i) : SV_Target
            {
                float2 uv = LatLong(i.dir);
                half3 night = SampleHdr(_NightTex, _NightTex_HDR, uv) * _NightExposure;
                half3 day = SampleHdr(_DayTex, _DayTex_HDR, uv) * _DayExposure;
                half3 col = lerp(night, day, _DayBlend);
                float h = normalize(i.dir).y;
                col += _HorizonGlow.rgb * pow(saturate(1.0 - abs(h) * 3.0), 3.0) * (1.0 - _DayBlend);
                col = lerp(col, unity_FogColor.rgb, _HorizonFog * pow(saturate(1.0 - abs(h) * 5.0), 2.0));
                return half4(col, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
