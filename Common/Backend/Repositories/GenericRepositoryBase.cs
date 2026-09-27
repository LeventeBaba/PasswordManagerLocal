using Microsoft.EntityFrameworkCore;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using System.Diagnostics.CodeAnalysis;

namespace PasswordManagerLocal.Common.Backend.Repositories;

public abstract class GenericRepositoryBase<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T> : IGenericRepository<T> where T : class
{
    protected readonly DbSet<T> Set;

    protected GenericRepositoryBase(DbSet<T> set)
    {
        Set = set;
    }




    public virtual async Task<IReadOnlyList<T>> ListAllAsync(CancellationToken ct = default) =>
        await Set.AsNoTracking().ToListAsync(ct);


    public virtual Task AddAsync(T entity, CancellationToken ct = default) =>
        Set.AddAsync(entity, ct).AsTask();


    public virtual void Update(T entity) =>
        Set.Update(entity);


    public virtual void Delete(T entity) =>
        Set.Remove(entity);


    public abstract Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default);


    public virtual async Task<(bool found, T? entity)> TryGetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await GetByIdAsync(id, ct);
        return (entity is not null, entity);
    }


    public virtual async Task<bool> ExistsAsync(Guid id, CancellationToken ct = default)
    {
        var (found, entity) = await TryGetByIdAsync(id, ct);
        return found;
    }
}