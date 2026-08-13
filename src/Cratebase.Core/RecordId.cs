namespace Cratebase.Core;

/// <summary>
/// Identifiant d'un enregistrement : un UUID version 7.
/// </summary>
/// <remarks>
/// <para>
/// PocketBase utilise des identifiants de 15 caractères aléatoires. Cratebase leur préfère l'UUIDv7
/// pour deux raisons : il est <b>triable dans l'ordre de création</b>, ce qui évite un index
/// supplémentaire sur la date pour paginer, et il est standard, donc `uuid` natif sur PostgreSQL
/// plutôt qu'un texte opaque.
/// </para>
/// <para>
/// L'identifiant est toujours produit par l'application, jamais par le moteur : c'est la règle R4
/// du document de conception. Un identifiant produit par la base rendrait l'insertion en lot
/// dépendante de <c>last_insert_rowid</c>, qui n'existe pas des deux côtés.
/// </para>
/// </remarks>
public readonly record struct RecordId(Guid Value) : IComparable<RecordId>
{
    /// <summary>Identifiant vide, qui ne désigne aucun enregistrement.</summary>
    public static readonly RecordId Empty = new(Guid.Empty);

    /// <summary>Produit un nouvel identifiant, croissant dans le temps.</summary>
    public static RecordId New() => new(Guid.CreateVersion7());

    /// <summary>Indique si l'identifiant ne désigne aucun enregistrement.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <summary>
    /// Analyse la forme canonique. Refuse toute autre forme que les 36 caractères minuscules avec
    /// tirets : accepter les variantes ferait diverger la comparaison textuelle en SQLite.
    /// </summary>
    public static bool TryParse(string? raw, out RecordId id)
    {
        id = Empty;

        if (raw is not { Length: 36 } || !Guid.TryParseExact(raw, "D", out var guid))
        {
            return false;
        }

        id = new RecordId(guid);
        return true;
    }

    /// <summary>Analyse la forme canonique, ou lève.</summary>
    public static RecordId Parse(string raw) => TryParse(raw, out var id)
        ? id
        : throw new ArgumentException($"« {raw} » n'est pas un identifiant valide.", nameof(raw));

    /// <inheritdoc />
    public int CompareTo(RecordId other) => Value.CompareTo(other.Value);

    /// <summary>Forme canonique : 36 caractères minuscules avec tirets.</summary>
    public override string ToString() => Value.ToString("D");

    public static bool operator <(RecordId left, RecordId right) => left.CompareTo(right) < 0;

    public static bool operator <=(RecordId left, RecordId right) => left.CompareTo(right) <= 0;

    public static bool operator >(RecordId left, RecordId right) => left.CompareTo(right) > 0;

    public static bool operator >=(RecordId left, RecordId right) => left.CompareTo(right) >= 0;
}
