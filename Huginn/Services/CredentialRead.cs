namespace Huginn.Services;

/// <summary>What reading a stored secret found.</summary>
public enum CredentialRead
{
    Found,

    /// <summary>Nothing is stored under that name.</summary>
    Absent,

    /// <summary>Something may be stored, but it could not be read just now.</summary>
    Failed,
}
