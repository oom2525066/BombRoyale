// testproj2のUnlitColor.shaderをURP向けに移植したもの。Unity組み込みの"Unlit/Color"は、ビルドに
// 含まれるMaterialから参照されているか「Always Included Shaders」に登録されていない限りストリップされ
// Shader.Find()がnullを返す不具合がtestproj2の実機ビルドで確認済み(ProceduralUnlitColor.cs参照)。
// Resources/以下に置くことで参照の有無に関わらず常にビルドへ含まれるようにする。
// FallbackCamera(ホーム画面ステージ)の背景/プレースホルダーモデル描画で使う。
Shader "Custom/ProceduralUnlitColor"
{
    Properties
    {
        _Color ("Color", Color) = (1, 1, 1, 1)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }

        Pass
        {
            Name "Unlit"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                return _Color;
            }
            ENDHLSL
        }
    }
}
