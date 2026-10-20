Shader "Hidden/TIC/TemperatureMask"
{
    Properties
    {
        _Temperature ("Temperature", Range(0, 1)) = 0
        _TemperatureCelsius ("Surface Temperature (C)", Float) = 21
    }

    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }

        Pass
        {
            Name "TemperatureMask"
            Tags { "LightMode"="UniversalForward" }
            Cull Back
            ZWrite Off
            ZTest LEqual
            Blend One One
            BlendOp Max
            ColorMask R

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            CBUFFER_START(UnityPerMaterial)
                float _Temperature;
                float _TemperatureCelsius;
            CBUFFER_END

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                VertexPositionInputs positions = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = positions.positionCS;
                output.positionWS = positions.positionWS;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float objectHeat = saturate(_Temperature);
                clip(objectHeat - 0.004);

                // One object temperature produces one consistent TIC reading over
                // the complete rendered surface. Heat propagation happens between
                // objects in ThermalRadiationManager, not as radial screen spots.
                return half4(max(_TemperatureCelsius, 21.0), 0, 0, 1);
            }
            ENDHLSL
        }
    }
}
