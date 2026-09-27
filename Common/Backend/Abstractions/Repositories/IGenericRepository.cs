namespace PasswordManagerLocal.Common.Backend.Abstractions.Repositories;

public interface IGenericRepository<T> where T : class
{
    Task<IReadOnlyList<T>> ListAllAsync(CancellationToken ct = default);
    Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<(bool found, T? entity)> TryGetByIdAsync(Guid id, CancellationToken ct = default);
    Task AddAsync(T entity, CancellationToken ct = default);
    void Update(T entity);
    void Delete(T entity);
    Task<bool> ExistsAsync(Guid id, CancellationToken ct = default);
}