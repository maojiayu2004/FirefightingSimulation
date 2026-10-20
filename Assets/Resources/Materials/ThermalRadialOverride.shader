Shader "Thermal/Thermal Radial Override"
{
    Properties
    {
        _Temperature ("Temperature", Range(0, 1)) = 0
        _TemperatureCelsius ("Surface Temperature (C)", Float) = 21
        _HeatOrigin ("Heat Origin", Vector) = (0, 0, 0, 0)
        _HeatRadius ("Heat Radius", Float) = 100000
        _UseLocalizedHeat ("Use Localized Heat", Float) = 0
        _UseAlarmColor ("Use Alarm Color", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
        }

        Pass
        {
            Name "UniversalForward"
            Tags { "LightMode" = "UniversalForward" }

            Cull Back
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                half3 normalWS : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            CBUFFER_START(UnityPerMaterial)
                float _Temperature;
                float _TemperatureCelsius;
                float4 _HeatOrigin;
                float _HeatRadius;
                float _UseLocalizedHeat;
                float _UseAlarmColor;
            CBUFFER_END

            Varyings Vertex(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionHCS = positionInputs.positionCS;
                output.positionWS = positionInputs.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return output;
            }

            // Fire-service TICs are primarily white-hot greyscale. Colour is an
            // alarm overlay reserved for dangerous temperatures, not a rainbow
            // palette applied to the whole scene.
            half3 SampleHeatIndicator(float celsius, half greyscale)
            {
                const half3 yellow = half3(1.00, 0.78, 0.02);
                const half3 orange = half3(1.00, 0.27, 0.00);
                const half3 red = half3(1.00, 0.12, 0.06);
                const half3 saturated = half3(1.00, 0.08, 0.04);

                half3 baseTone = greyscale.xxx;
                if (celsius < 150.0)
                    return baseTone;

                half3 warning;
                if (celsius < 300.0)
                    warning = lerp(yellow, orange, smoothstep(150.0, 300.0, celsius));
                else if (celsius < 450.0)
                    warning = lerp(orange, red, smoothstep(300.0, 450.0, celsius));
                else
                    warning = lerp(red, saturated, smoothstep(450.0, 650.0, celsius));

                // Retain some luminance detail within coloured fire/hot objects.
                return warning * lerp(0.76, 1.12, greyscale);
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                float heat = saturate(_Temperature);
                float celsius = max(21.0, _TemperatureCelsius);

                if (_UseLocalizedHeat > 0.5)
                {
                    // The available assets do not provide authored thermal maps.
                    // Use a restrained, continuous surface variation instead of
                    // fake spherical bands around each ignition point.
                    float scale = rcp(max(_HeatRadius, 0.35));
                    float3 thermalPosition = input.positionWS * scale;
                    float broadVariation = sin(thermalPosition.x * 1.37 + thermalPosition.y * 0.73)
                                         * sin(thermalPosition.z * 1.11 - thermalPosition.y * 0.49);
                    float fineVariation = sin(dot(thermalPosition, float3(2.31, -1.67, 1.93)));
                    float surfaceResponse = saturate(0.89 + broadVariation * 0.055 + fineVariation * 0.025);
                    heat *= surfaceResponse;
                    celsius = lerp(21.0, celsius, surfaceResponse);
                }

                // The overlay feature visits the complete opaque scene. Ambient
                // objects discard here, leaving the white-hot base untouched.
                clip(heat - 0.006);

                // This pass is composited only over thermally active objects. The
                // full-screen pass underneath retains the scene's actual detail.
                half thermalLift = smoothstep(21.0, 150.0, celsius);
                half greyscale = lerp(0.48, 1.0, thermalLift);

                half3 color = _UseAlarmColor > 0.5
                    ? SampleHeatIndicator(celsius, greyscale)
                    : greyscale.xxx;
                // Let the structural white-hot image remain visible through the
                // warning colour, as it does on firefighter TIC overlays.
                half opacity = lerp(0.58, 0.78, saturate(heat));
                return half4(color, opacity);
            }
            ENDHLSL
        }
    }
}
