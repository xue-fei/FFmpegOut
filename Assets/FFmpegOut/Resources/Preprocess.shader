Shader "Hidden/FFmpegOut/Preprocess"
{
    Properties
    {
        _MainTex("", 2D) = "white" {}
    }

    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

    TEXTURE2D(_MainTex);
    SAMPLER(sampler_MainTex);

    // Needed for SRP Batcher / full-screen triangle
    struct Attributes
    {
        uint vertexID : SV_VertexID;
    };

    struct Varyings
    {
        float4 positionCS : SV_POSITION;
        float2 uv         : TEXCOORD0;
    };

    Varyings Vert(Attributes input)
    {
        Varyings output;
        // Full-screen triangle (no vertex buffer needed)
        output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
        output.uv         = GetFullScreenTriangleTexCoord(input.vertexID);
        return output;
    }

    float4 FragFlip(Varyings input) : SV_Target
    {
        float2 uv = input.uv;
        uv.y = 1.0 - uv.y;          // vertical flip (OpenGL → D3D convention)
        return SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv);
    }
    ENDHLSL

    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragFlip
            ENDHLSL
        }
    }
}