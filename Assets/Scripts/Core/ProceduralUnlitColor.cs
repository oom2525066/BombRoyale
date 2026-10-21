using UnityEngine;

/// <summary>
/// 単色マテリアルを実行時生成するヘルパー。ビルドでストリップされない Resources/Shaders/UnlitColor を使う。
/// </summary>
public static class ProceduralUnlitColor
{
    private static Shader _shader;

    public static Material CreateMaterial(Color color)
    {
        if (_shader == null) _shader = Resources.Load<Shader>("Shaders/UnlitColor");

        if (_shader == null)
        {
            // 見つからない場合は常時内蔵のInternalErrorShaderで代用(マゼンタ表示になるが例外で止まらない)
            Debug.LogWarning("[ProceduralUnlitColor] Resources/Shaders/UnlitColor.shaderが見つかりません。フォールバックシェーダーで代用します。");
            var fallback = new Material(Shader.Find("Hidden/InternalErrorShader"));
            fallback.color = color;
            return fallback;
        }

        var material = new Material(_shader);
        material.color = color;
        return material;
    }
}
