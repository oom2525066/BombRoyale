// 【ユーザー要望】「自分以外のプレイヤーにアウトライン出せない?できればカスタマイズも」への対応。
// いわゆる「裏面反転(inverted hull)」方式のアウトライン: 法線方向へ少し膨らませたコピーを
// 裏面だけ(Cull Front)描画することで、元のメッシュのシルエットの外側にだけ縁取りが見える。
// SkinnedMeshRendererはUnityがスキニング(ボーン変形)をシェーダーより前段で適用するため、
// 特別なスキニング対応コードは不要(通常のUniversal Render Pipeline/Litマテリアルと同じ扱いでよい)。
// PlayerController.SetupOutlineIfNeeded()がOwner以外のプレイヤーにだけ、既存のSkinnedMeshRendererの
// マテリアル配列へこのシェーダーのマテリアルを追加パスとして足すことで有効化する。
Shader "BombRoyale/PlayerOutline"
{
    Properties
    {
        _OutlineColor("Outline Color", Color) = (1, 0.85, 0.2, 1)
        _OutlineWidth("Outline Width", Range(0, 0.1)) = 0.02
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry+1" }

        Pass
        {
            Name "Outline"
            Cull Front
            ZWrite On

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _OutlineColor;
                float _OutlineWidth;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                float3 expandedPositionOS = IN.positionOS.xyz + normalize(IN.normalOS) * _OutlineWidth;
                OUT.positionHCS = TransformObjectToHClip(expandedPositionOS);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                return _OutlineColor;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
