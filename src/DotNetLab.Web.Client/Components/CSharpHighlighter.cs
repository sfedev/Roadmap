using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;

namespace DotNetLab.Web.Client.Components;

/// <summary>
/// Resaltador de sintaxis C# escrito en C#: tokeniza el código y emite spans con clases CSS.
/// No hay librería JS externa, así que funciona sin CDN (compatible con una CSP estricta)
/// y el coloreado ocurre en el mismo runtime que el resto del componente.
/// </summary>
// 'partial' porque [GeneratedRegex] hace que el compilador escriba la implementación del
// autómata en tiempo de compilación: sin Reflection.Emit, que WebAssembly no soporta.
public static partial class CSharpHighlighter
{
    // Un único recorrido con alternancia ordenada: el orden IMPORTA. Los comentarios y las
    // cadenas van primero para que una palabra clave dentro de un string no se coloree como tal.
    [GeneratedRegex(
        """
        (?<cmt>//[^\r\n]*)|(?<str>@?"(?:[^"\\\r\n]|\\.)*")|(?<chr>'(?:[^'\\\r\n]|\\.)')|(?<num>\b\d[\d_]*(?:\.\d+)?[fFdDmMuUlL]?\b)|(?<kw>\b(?:abstract|as|async|await|base|bool|break|byte|case|catch|char|class|const|continue|default|do|double|else|enum|false|finally|float|for|foreach|get|global|if|in|init|int|interface|internal|is|lock|long|namespace|new|not|null|object|or|out|override|params|partial|private|protected|public|readonly|record|ref|return|sealed|set|static|string|struct|switch|this|throw|true|try|typeof|uint|ulong|using|value|var|virtual|void|when|where|while|with|yield)\b)|(?<typ>\b[A-Z][A-Za-z0-9_]*\b)
        """,
        // IgnoreCase NO: en C# 'String' y 'string' son tokens visualmente distintos.
        RegexOptions.ExplicitCapture)]
    private static partial Regex Tokenizer();

    /// <summary>Convierte código C# en HTML resaltado listo para inyectar en un &lt;pre&gt;.</summary>
    public static MarkupString Highlight(string code)
    {
        // Capacidad estimada: el HTML resultante ronda el doble del original por los <span>.
        var builder = new StringBuilder(code.Length * 2);
        // Cursor sobre el texto original: marca dónde termina el último token emitido.
        var cursor = 0;

        foreach (var match in Tokenizer().EnumerateMatches(code))
        {
            // Texto entre tokens (espacios, operadores, puntuación): se emite escapado y sin clase.
            // Escapar SIEMPRE es lo que impide que el código de ejemplo se interprete como HTML.
            builder.Append(WebUtility.HtmlEncode(code[cursor..match.Index]));

            // EnumerateMatches devuelve solo posiciones (ValueMatch), sin asignar objetos Match.
            // Para saber qué grupo casó hay que re-evaluar el fragmento, que es corto y barato.
            var token = code.Substring(match.Index, match.Length);
            builder.Append("<span class=\"tok-").Append(ClassifyToken(token)).Append("\">")
                   .Append(WebUtility.HtmlEncode(token))
                   .Append("</span>");

            cursor = match.Index + match.Length;
        }

        // Cola posterior al último token; sin esto se perdería el final del snippet.
        builder.Append(WebUtility.HtmlEncode(code[cursor..]));

        // MarkupString le dice a Blazor "esto ya es HTML seguro, no lo vuelvas a escapar".
        // Es seguro porque cada fragmento se ha pasado por HtmlEncode justo arriba.
        return new MarkupString(builder.ToString());
    }

    /// <summary>Deduce la clase CSS a partir de la forma del token ya capturado.</summary>
    // Switch expression con patrones sobre el propio texto: más legible que consultar grupos.
    private static string ClassifyToken(string token) => token switch
    {
        // Comentario de línea: empieza por '//'.
        ['/', '/', ..] => "cmt",
        // Cadena verbatim (@"...") o normal ("...").
        ['@', '"', ..] or ['"', ..] => "str",
        // Literal de carácter.
        ['\'', ..] => "str",
        // Empieza por dígito => literal numérico.
        [>= '0' and <= '9', ..] => "num",
        // Empieza por mayúscula => tipo, atributo o miembro (convención de C#).
        [>= 'A' and <= 'Z', ..] => "typ",
        // El resto de lo que capturó el tokenizador solo puede ser una palabra clave.
        _ => "kw"
    };
}
