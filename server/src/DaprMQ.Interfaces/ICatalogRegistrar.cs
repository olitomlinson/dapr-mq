namespace DaprMQ.Interfaces;

/// <summary>
/// Worker-local, non-blocking hand-off of catalog entries from actors to the catalog. Registering
/// never adds a cross-actor call to the caller's turn: entries are batched and sent in the background.
/// </summary>
public interface ICatalogRegistrar
{
    void Register(CatalogEntry entry);
}
