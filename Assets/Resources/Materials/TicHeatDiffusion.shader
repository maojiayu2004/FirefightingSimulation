Shader "Hidden/TIC/HeatDiffusion"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off

        HLSLINCLUDE
        #pragma target 3.5
        #pragma vertex Vert
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        half2 SampleHeat(float2 uv) { return SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv, 0).rg; }

        half4 DilateAxis(Varyings input, float2 axis, float radius) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            float2 stepUV = axis * _BlitTexture_TexelSize.xy * radius;
            half2 heat = SampleHeat(input.texcoord);
            heat = max(heat, SampleHeat(input.texcoord + stepUV));
            heat = max(heat, SampleHeat(input.texcoord - stepUV));
            heat = max(heat, SampleHeat(input.texcoord + stepUV * 2.0));
            heat = max(heat, SampleHeat(input.texcoord - stepUV * 2.0));
            heat = max(heat, SampleHeat(input.texcoord + stepUV * 3.0));
            heat = max(heat, SampleHeat(input.texcoord - stepUV * 3.0));
            return half4(heat, 0, 1);
        }

        half4 BlurAxis(Varyings input, float2 axis, float radius) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            float2 stepUV = axis * _BlitTexture_TexelSize.xy * radius;
            half2 heat = SampleHeat(input.texcoord) * 0.24;
            heat += SampleHeat(input.texcoord + stepUV) * 0.20;
            heat += SampleHeat(input.texcoord - stepUV) * 0.20;
            heat += SampleHeat(input.texcoord + stepUV * 2.0) * 0.13;
            heat += SampleHeat(input.texcoord - stepUV * 2.0) * 0.13;
            heat += SampleHeat(input.texcoord + stepUV * 3.0) * 0.05;
            heat += SampleHeat(input.texcoord - stepUV * 3.0) * 0.05;
            return half4(heat, 0, 1);
        }
        ENDHLSL

        Pass
        {
            Name "Close Gaps Horizontal"
            HLSLPROGRAM
            half4 Frag(Varyings input) : SV_Target { return DilateAxis(input, float2(1, 0), 3.0); }
            #pragma fragment Frag
            ENDHLSL
        }

        Pass
        {
            Name "Close Gaps Vertical"
            HLSLPROGRAM
            half4 Frag(Varyings input) : SV_Target { return DilateAxis(input, float2(0, 1), 3.0); }
            #pragma fragment Frag
            ENDHLSL
        }

        Pass
        {
            Name "Soft Horizontal"
            HLSLPROGRAM
            half4 Frag(Varyings input) : SV_Target { return BlurAxis(input, float2(1, 0), 1.75); }
            #pragma fragment Frag
            ENDHLSL
        }

        Pass
        {
            Name "Soft Vertical"
            HLSLPROGRAM
            half4 Frag(Varyings input) : SV_Target { return BlurAxis(input, float2(0, 1), 1.75); }
            #pragma fragment Frag
            ENDHLSL
        }
    }
}
