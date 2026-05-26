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
        output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
        output.uv         = GetFullScreenTriangleTexCoord(input.vertexID);
        return output;
    }

    float3 LinearToSRGB_Fast(float3 linearCol)
    {
        float3 sRGBLo = linearCol * 12.92;
        float3 sRGBHi = (pow(abs(linearCol), float3(1.0 / 2.4, 1.0 / 2.4, 1.0 / 2.4)) * 1.055) - 0.055;
        float3 sRGB = (linearCol <= 0.0031308) ? sRGBLo : sRGBHi;
        return sRGB;
    }

    float4 FragFlip(Varyings input) : SV_Target
    {
        float2 uv = input.uv;
        uv.y = 1.0 - uv.y;
        float4 col = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv);
        col.rgb = LinearToSRGB_Fast(col.rgb);
        return col;
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
