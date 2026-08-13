using System.Text;

namespace Cratebase.Data;

/// <summary>
/// Construction des motifs de l'opérateur <c>~</c>.
/// </summary>
/// <remarks>
/// L'opérateur enrobe automatiquement l'opérande de droite de jokers, comme chez PocketBase :
/// <c>title ~ 'bonjour'</c> cherche <c>%bonjour%</c>. Il faut donc <b>échapper les jokers que
/// l'utilisateur a lui-même écrits</b>, sans quoi une recherche sur « 100% » ou sur « a_b » ramène
/// n'importe quoi — et, sur une règle d'accès, élargit le périmètre.
/// </remarks>
public static class LikePattern
{
    /// <summary>Caractère d'échappement, déclaré par la clause <c>ESCAPE</c>.</summary>
    public const char EscapeCharacter = '\\';

    /// <summary>
    /// Construit un motif « contient », jokers de l'utilisateur échappés.
    /// </summary>
    public static string Contains(string? value) => "%" + Escape(value) + "%";

    /// <summary>Échappe les métacaractères de <c>LIKE</c> dans une valeur.</summary>
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length + 8);

        foreach (var current in value)
        {
            if (current is '%' or '_' or EscapeCharacter)
            {
                builder.Append(EscapeCharacter);
            }

            builder.Append(current);
        }

        return builder.ToString();
    }
}
