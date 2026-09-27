using PasswordManagerLocal.Common.Backend.Constants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.Persistence;

public class AppDbContext : DbContext
{
    private static readonly ValueConverter<DateTime, DateTime> UtcDateTimeConverter = new(
        value => UtcDateTimeUtil.ToUtc(value),
        value => UtcDateTimeUtil.ToUtc(value));


    private static readonly ValueConverter<DateTimeOffset, DateTimeOffset> UtcDateTimeOffsetConverter = new(
        value => UtcDateTimeUtil.ToUtc(value),
        value => UtcDateTimeUtil.ToUtc(value));


    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<UserDevice> UserDevices => Set<UserDevice>();
    public DbSet<SyncItem> SyncItems => Set<SyncItem>();
    public DbSet<SyncQueueItem> SyncQueueItems => Set<SyncQueueItem>();
    public DbSet<SyncTombstone> SyncTombstones => Set<SyncTombstone>();
    public DbSet<LocalDeviceIdentity> LocalDeviceIdentities => Set<LocalDeviceIdentity>();
    public DbSet<LocalUserDevice> LocalUserDevices => Set<LocalUserDevice>();
    public DbSet<UserSyncSnapshot> UserSyncSnapshots => Set<UserSyncSnapshot>();
    public DbSet<UserSyncState> UserSyncStates => Set<UserSyncState>();
    public DbSet<UserRevisionKnowledge> UserRevisionKnowledge => Set<UserRevisionKnowledge>();
    public DbSet<UserControlOperation> UserControlOperations => Set<UserControlOperation>();
    public DbSet<UserControlState> UserControlStates => Set<UserControlState>();
    public DbSet<UserMembershipAuthorization> UserMembershipAuthorizations => Set<UserMembershipAuthorization>();
    public DbSet<UserOriginRemovalCutoff> UserOriginRemovalCutoffs => Set<UserOriginRemovalCutoff>();
    public DbSet<DeviceEnrollmentCommit> DeviceEnrollmentCommits => Set<DeviceEnrollmentCommit>();
    public DbSet<DeletedUserBarrier> DeletedUserBarriers => Set<DeletedUserBarrier>();
    public DbSet<SyncVersionClockState> SyncVersionClockStates => Set<SyncVersionClockState>();
    public DbSet<UserLoginIdentityState> UserLoginIdentityStates => Set<UserLoginIdentityState>();
    public DbSet<UserCanonicalCheckpoint> UserCanonicalCheckpoints => Set<UserCanonicalCheckpoint>();
    public DbSet<UserSyncFault> UserSyncFaults => Set<UserSyncFault>();

    public override int SaveChanges()
    {
        GenerateDerivedValuesAndRelationshipIntegrityHashes();
        return base.SaveChanges(true);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GenerateDerivedValuesAndRelationshipIntegrityHashes();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        GenerateDerivedValuesAndRelationshipIntegrityHashes();
        return base.SaveChangesAsync(true, cancellationToken);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        GenerateDerivedValuesAndRelationshipIntegrityHashes();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }


    private void ValidateNoDeletedUserResurrection()
    {
        var mutableUserIds = ChangeTracker.Entries<User>()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified)
            .Select(entry => entry.Entity.UId)
            .Concat(ChangeTracker.Entries<UserLoginIdentityState>()
                .Where(entry => entry.State is EntityState.Added or EntityState.Modified)
                .Select(entry => entry.Entity.UserId))
            .Where(userId => userId != Guid.Empty)
            .Distinct()
            .ToList();
        if (mutableUserIds.Count == 0)
            return;

        var trackedBarrierIds = ChangeTracker.Entries<DeletedUserBarrier>()
            .Where(entry => entry.State != EntityState.Deleted)
            .Select(entry => entry.Entity.UserId)
            .ToHashSet();
        if (mutableUserIds.Any(trackedBarrierIds.Contains) ||
            DeletedUserBarriers.AsNoTracking().Any(barrier => mutableUserIds.Contains(barrier.UserId)))
        {
            throw new InvalidOperationException("A permanent account-deletion barrier prevents recreating or updating this user identity.");
        }
    }

    private void GenerateDerivedValuesAndRelationshipIntegrityHashes()
    {
        ValidateNoDeletedUserResurrection();
        NormalizeTrackedUtcDateTimes();

        foreach (var entry in ChangeTracker.Entries<Device>()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            entry.Entity.SignPublicKeyHash = entry.Entity.SignPublicKey.Length == 0
                ? []
                : Security.Hashing.SHA256Hash(entry.Entity.SignPublicKey);
            entry.Entity.GenerateIntegrityHash();
        }

        foreach (var entry in ChangeTracker.Entries<UserDevice>()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            entry.Entity.ModelId = Sync.SyncIdentityUtil.BuildUserDeviceModelId(entry.Entity.UserId, entry.Entity.DeviceId);
            entry.Entity.GenerateIntegrityHash();
        }

        foreach (var entry in ChangeTracker.Entries<LocalUserDevice>()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
            entry.Entity.GenerateIntegrityHash();
    }


    private void NormalizeTrackedUtcDateTimes()
    {
        foreach (var entry in ChangeTracker.Entries()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
            UtcDateTimeUtil.NormalizeDateTimeProperties(entry.Entity);
    }


    private static void ApplyUtcDateTimeConverters(ModelBuilder model)
    {
        foreach (var entityType in model.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTime))
                    property.SetValueConverter(UtcDateTimeConverter);
                else if (property.ClrType == typeof(DateTimeOffset))
                    property.SetValueConverter(UtcDateTimeOffsetConverter);
            }
        }
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);

        model.Entity<User>().HasKey(u => u.UId);
        model.Entity<Group>().HasKey(g => g.Id);

        var device = model.Entity<Device>();
        device.HasKey(d => d.Id);
        device.Property(d => d.PublicKey).IsRequired();
        device.Property(d => d.SignPublicKey).IsRequired();
        device.Property(d => d.SignPublicKeyHash).IsRequired();
        device.Property(d => d.TlsCertFingerprint).IsRequired().HasMaxLength(128);
        device.Property(d => d.DeviceType).HasConversion<byte>().IsRequired();
        device.Property(d => d.BlockedReason).HasMaxLength(512);
        device.HasIndex(d => d.TlsCertFingerprint).IsUnique();
        device.HasIndex(d => d.SignPublicKeyHash);

        var syncItem = model.Entity<SyncItem>();
        syncItem.HasKey(si => si.Id);
        syncItem.HasIndex(si => new { si.ModelId, si.ModelType }).IsUnique();

        var queue = model.Entity<SyncQueueItem>();
        queue.ToTable("SyncQueueItems");
        queue.HasKey(q => q.QueueId);
        queue.Property(q => q.QueueId).ValueGeneratedOnAdd();
        queue.HasAlternateKey(q => q.Id);
        queue.Property(q => q.EnqueuedAt).IsRequired();
        queue.HasOne(q => q.Device)
            .WithMany(d => d.ItemsNeedingSync)
            .HasForeignKey(q => q.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);
        queue.HasOne(q => q.SyncItem)
            .WithMany(si => si.QueueItems)
            .HasForeignKey(q => q.SyncItemId)
            .OnDelete(DeleteBehavior.Cascade);
        queue.HasIndex(q => new { q.ProcessedAt, q.QueueId });
        queue.HasIndex(q => new { q.DeviceId, q.ProcessedAt, q.QueueId });
        queue.HasIndex(q => new { q.DeviceId, q.SyncItemId }).IsUnique();

        var user = model.Entity<User>();
        user.Property(u => u.EncryptedPayload).IsRequired().IsConcurrencyToken();
        user.Property(u => u.EncryptedGeneralUserDataPayload).IsRequired().IsConcurrencyToken();
        user.Property(u => u.EncryptedUserPasswordsDataPayload).IsRequired().IsConcurrencyToken();
        user.Property(u => u.EncryptedUserDevicesDataPayload).IsRequired().IsConcurrencyToken();
        user.Property(u => u.UserDataLastModifiedAt).IsRequired();
        user.Property(u => u.GeneralUserDataLastModifiedAt).IsRequired();
        user.Property(u => u.UserPasswordsDataLastModifiedAt).IsRequired();
        user.Property(u => u.UserDevicesDataLastModifiedAt).IsRequired();
        user.Property(u => u.KeyEpoch).IsRequired().IsConcurrencyToken();
        user.Property(u => u.MembershipEpoch).IsRequired().IsConcurrencyToken();
        user.Property(u => u.GeneralDataVersionPhysicalTimeUnixMilliseconds).IsRequired();
        user.Property(u => u.GeneralDataVersionLogicalCounter).IsRequired();
        user.Property(u => u.GeneralDataVersionOriginDeviceId).IsRequired();
        user.Property(u => u.GeneralDataVersionOriginInstanceId).IsRequired();

        model.Entity<User>()
            .HasMany(u => u.Groups)
            .WithMany(g => g.Users)
            .UsingEntity(j => j.ToTable("GroupMembers"));

        var userDevice = model.Entity<UserDevice>();
        userDevice.ToTable("UserDevices");
        userDevice.HasKey(ud => new { ud.UserId, ud.DeviceId });
        userDevice.HasOne(ud => ud.User)
            .WithMany(u => u.UserDevices)
            .HasForeignKey(ud => ud.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        userDevice.HasOne(ud => ud.Device)
            .WithMany(d => d.UserDevices)
            .HasForeignKey(ud => ud.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);
        userDevice.Property(ud => ud.ModelId).IsRequired();
        userDevice.Property(ud => ud.LastModifiedAt).IsRequired();
        userDevice.Property(ud => ud.IntegrityHash).IsRequired();
        userDevice.Property(ud => ud.IsSyncOn).IsRequired();
        userDevice.Property(ud => ud.IsDeleted).IsRequired();
        userDevice.HasIndex(ud => ud.ModelId).IsUnique();
        userDevice.HasIndex(ud => ud.DeviceId);
        userDevice.HasIndex(ud => new { ud.UserId, ud.IsDeleted, ud.IsSyncOn });
        userDevice.HasIndex(ud => new { ud.DeviceId, ud.IsDeleted });

        var localUserDevice = model.Entity<LocalUserDevice>();
        localUserDevice.ToTable("LocalUserDevices");
        localUserDevice.HasKey(x => new { x.UserId, x.LocalDeviceIdentityId });
        localUserDevice.HasOne(x => x.User)
            .WithMany(u => u.LocalUserDevices)
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        localUserDevice.HasOne(x => x.LocalDeviceIdentity)
            .WithMany(d => d.LocalUsers)
            .HasForeignKey(x => x.LocalDeviceIdentityId)
            .OnDelete(DeleteBehavior.Cascade);
        localUserDevice.Property(x => x.IsSyncOn).IsRequired().HasDefaultValue(true);
        localUserDevice.Property(x => x.IntegrityHash).IsRequired();
        localUserDevice.HasIndex(x => x.UserId).IsUnique();
        localUserDevice.HasIndex(x => x.IsSyncOn);

        var tombstone = model.Entity<SyncTombstone>();
        tombstone.HasKey(t => t.Id);
        tombstone.HasIndex(t => new { t.ModelId, t.ModelType }).IsUnique();
        tombstone.Property(t => t.DeletedAtTs).IsRequired();



        var loginIdentity = model.Entity<UserLoginIdentityState>();
        loginIdentity.ToTable("UserLoginIdentityStates");
        loginIdentity.HasKey(identity => identity.UserId);
        loginIdentity.Property(identity => identity.UsernameHash).IsRequired().HasMaxLength(CryptographyConstants.Sha256HashSizeInBytes);
        loginIdentity.Property(identity => identity.UsernameSalt).IsRequired().HasMaxLength(CryptographyConstants.Sha256HashSizeInBytes);
        loginIdentity.Property(identity => identity.SourceSnapshotHash).IsRequired().HasMaxLength(Constants.SyncConstants.SyncDeltaPayloadHashBytes);
        loginIdentity.Property(identity => identity.Status).HasConversion<byte>().IsRequired();
        loginIdentity.Property(identity => identity.StatusReason).HasMaxLength(512);
        loginIdentity.Property(identity => identity.ConcurrencyVersion).IsRequired().IsConcurrencyToken();
        loginIdentity.HasOne<User>()
            .WithOne()
            .HasForeignKey<UserLoginIdentityState>(identity => identity.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        loginIdentity.HasIndex(identity => identity.Status);
        loginIdentity.HasIndex(identity => new { identity.KeyEpoch, identity.MembershipEpoch });

        var canonicalCheckpoint = model.Entity<UserCanonicalCheckpoint>();
        canonicalCheckpoint.ToTable("UserCanonicalCheckpoints");
        canonicalCheckpoint.HasKey(checkpoint => checkpoint.UserId);
        canonicalCheckpoint.Property(checkpoint => checkpoint.CheckpointSequence).IsRequired().IsConcurrencyToken();
        canonicalCheckpoint.Property(checkpoint => checkpoint.LocalDeviceId).IsRequired();
        canonicalCheckpoint.Property(checkpoint => checkpoint.LocalOriginInstanceId).IsRequired();
        canonicalCheckpoint.Property(checkpoint => checkpoint.KeyEpoch).IsRequired();
        canonicalCheckpoint.Property(checkpoint => checkpoint.MembershipEpoch).IsRequired();
        canonicalCheckpoint.Property(checkpoint => checkpoint.CanonicalContentHash).IsRequired().HasMaxLength(Constants.SyncConstants.SyncDeltaPayloadHashBytes).IsConcurrencyToken();
        canonicalCheckpoint.Property(checkpoint => checkpoint.UserIntegrityHash).IsRequired().HasMaxLength(Constants.SyncConstants.SyncDeltaPayloadHashBytes);
        canonicalCheckpoint.Property(checkpoint => checkpoint.SignPublicKey).IsRequired().HasMaxLength(Constants.SyncConstants.SyncDeltaEd25519PublicKeyBytes);
        canonicalCheckpoint.Property(checkpoint => checkpoint.Signature).IsRequired().HasMaxLength(Constants.SyncConstants.SyncDeltaEd25519SignatureBytes);
        canonicalCheckpoint.Property(checkpoint => checkpoint.CreatedAtUtc).IsRequired();
        canonicalCheckpoint.HasOne<User>()
            .WithOne()
            .HasForeignKey<UserCanonicalCheckpoint>(checkpoint => checkpoint.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        var syncFault = model.Entity<UserSyncFault>();
        syncFault.ToTable("UserSyncFaults");
        syncFault.HasKey(fault => fault.Id);
        syncFault.Property(fault => fault.Scope).HasConversion<byte>().IsRequired();
        syncFault.Property(fault => fault.Kind).HasConversion<byte>().IsRequired();
        syncFault.Property(fault => fault.Status).HasConversion<byte>().IsRequired();
        syncFault.Property(fault => fault.AffectedComponent).IsRequired().HasMaxLength(128);
        syncFault.Property(fault => fault.ExpectedHash).IsRequired().HasMaxLength(Constants.SyncConstants.SyncDeltaPayloadHashBytes);
        syncFault.Property(fault => fault.ObservedHash).IsRequired().HasMaxLength(Constants.SyncConstants.SyncDeltaPayloadHashBytes);
        syncFault.Property(fault => fault.ConflictingHash).IsRequired().HasMaxLength(Constants.SyncConstants.SyncDeltaPayloadHashBytes);
        syncFault.Property(fault => fault.DiagnosticCode).IsRequired().HasMaxLength(128);
        syncFault.Property(fault => fault.FirstDetectedAtUtc).IsRequired();
        syncFault.Property(fault => fault.LastDetectedAtUtc).IsRequired();
        syncFault.Property(fault => fault.RecoveryAttemptCount).IsRequired();
        syncFault.Property(fault => fault.BlocksPublishing).IsRequired();
        syncFault.Property(fault => fault.BlocksMerge).IsRequired();
        syncFault.Property(fault => fault.BlocksLogin).IsRequired();
        syncFault.Property(fault => fault.BlocksGarbageCollection).IsRequired();
        syncFault.Property(fault => fault.BlocksLifecycle).IsRequired();
        syncFault.HasOne<User>()
            .WithMany()
            .HasForeignKey(fault => fault.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        syncFault.HasIndex(fault => new { fault.UserId, fault.Status, fault.Scope });
        syncFault.HasIndex(fault => new { fault.UserId, fault.Scope, fault.Kind, fault.OriginDeviceId, fault.OriginInstanceId, fault.KeyEpoch });

        var userSyncSnapshot = model.Entity<UserSyncSnapshot>();
        userSyncSnapshot.ToTable("UserSyncSnapshots");
        userSyncSnapshot.HasKey(snapshot => snapshot.Id);
        userSyncSnapshot.Property(snapshot => snapshot.OriginRevision).IsRequired().IsConcurrencyToken();
        userSyncSnapshot.Property(snapshot => snapshot.UserKeyEpoch).IsRequired();
        userSyncSnapshot.Property(snapshot => snapshot.MembershipEpoch).IsRequired();
        userSyncSnapshot.Property(snapshot => snapshot.CreatedAtUtc).IsRequired();
        userSyncSnapshot.Property(snapshot => snapshot.ReceivedAtUtc).IsRequired();
        userSyncSnapshot.Property(snapshot => snapshot.SnapshotHash).IsRequired().HasMaxLength(Constants.SyncConstants.SyncDeltaPayloadHashBytes).IsConcurrencyToken();
        userSyncSnapshot.Property(snapshot => snapshot.OriginSignPublicKey).IsRequired().HasMaxLength(Constants.SyncConstants.SyncDeltaEd25519PublicKeyBytes);
        userSyncSnapshot.Property(snapshot => snapshot.OriginSignature).IsRequired().HasMaxLength(Constants.SyncConstants.SyncDeltaEd25519SignatureBytes);
        userSyncSnapshot.Property(snapshot => snapshot.EnvelopePayload).IsRequired().HasMaxLength(Constants.SyncConstants.MaxUserSnapshotEnvelopeBytes);
        userSyncSnapshot.Property(snapshot => snapshot.Status).HasConversion<byte>().IsRequired();
        userSyncSnapshot.Property(snapshot => snapshot.QuarantineReason).HasMaxLength(512);
        userSyncSnapshot.Property(snapshot => snapshot.ConflictingSnapshotHash).HasMaxLength(Constants.SyncConstants.SyncDeltaPayloadHashBytes);
        userSyncSnapshot.HasOne<User>()
            .WithMany()
            .HasForeignKey(snapshot => snapshot.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        userSyncSnapshot.HasIndex(snapshot => new
        {
            snapshot.UserId,
            snapshot.OriginDeviceId,
            snapshot.OriginInstanceId,
            snapshot.UserKeyEpoch
        }).IsUnique();
        userSyncSnapshot.HasIndex(snapshot => new { snapshot.UserId, snapshot.Status, snapshot.ReceivedAtUtc });

        var userSyncState = model.Entity<UserSyncState>();
        userSyncState.ToTable("UserSyncStates");
        userSyncState.HasKey(state => state.UserId);
        userSyncState.Property(state => state.LocalOriginInstanceId).IsRequired();
        userSyncState.Property(state => state.NextOriginRevision).IsRequired().HasDefaultValue(1L).IsConcurrencyToken();
        userSyncState.Property(state => state.LastPublishedContentHash).IsRequired().HasMaxLength(Constants.SyncConstants.SyncDeltaPayloadHashBytes).IsConcurrencyToken();
        userSyncState.Property(state => state.LastUpdatedAtUtc).IsRequired();
        userSyncState.HasOne<User>()
            .WithOne()
            .HasForeignKey<UserSyncState>(state => state.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        var revisionKnowledge = model.Entity<UserRevisionKnowledge>();
        revisionKnowledge.ToTable("UserRevisionKnowledge");
        revisionKnowledge.HasKey(knowledge => new
        {
            knowledge.UserId,
            knowledge.OriginDeviceId,
            knowledge.OriginInstanceId,
            knowledge.UserKeyEpoch
        });
        revisionKnowledge.Property(knowledge => knowledge.HighestStoredRevision).IsRequired();
        revisionKnowledge.Property(knowledge => knowledge.HighestStoredSnapshotHash).IsRequired().HasMaxLength(Constants.SyncConstants.SyncDeltaPayloadHashBytes);
        revisionKnowledge.Property(knowledge => knowledge.HighestMergedRevision).IsRequired();
        revisionKnowledge.Property(knowledge => knowledge.LastUpdatedAtUtc).IsRequired();
        revisionKnowledge.HasOne<User>()
            .WithMany()
            .HasForeignKey(knowledge => knowledge.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        revisionKnowledge.HasIndex(knowledge => new { knowledge.UserId, knowledge.HighestStoredRevision });
        revisionKnowledge.HasIndex(knowledge => new { knowledge.UserId, knowledge.HighestMergedRevision });

        var controlOperation = model.Entity<UserControlOperation>();
        controlOperation.ToTable("UserControlOperations");
        controlOperation.HasKey(operation => operation.OperationId);
        controlOperation.Property(operation => operation.OperationType).HasConversion<byte>().IsRequired();
        controlOperation.Property(operation => operation.OriginSequence).IsRequired();
        controlOperation.Property(operation => operation.PreviousKeyEpoch).IsRequired();
        controlOperation.Property(operation => operation.ResultingKeyEpoch).IsRequired();
        controlOperation.Property(operation => operation.PreviousMembershipEpoch).IsRequired();
        controlOperation.Property(operation => operation.ResultingMembershipEpoch).IsRequired();
        controlOperation.Property(operation => operation.CreatedAtUtc).IsRequired();
        controlOperation.Property(operation => operation.ReceivedAtUtc).IsRequired();
        controlOperation.Property(operation => operation.PayloadHash).IsRequired().HasMaxLength(Constants.SyncConstants.SyncDeltaPayloadHashBytes);
        controlOperation.Property(operation => operation.OperationHash).IsRequired().HasMaxLength(Constants.SyncConstants.SyncDeltaPayloadHashBytes).IsConcurrencyToken();
        controlOperation.Property(operation => operation.OriginSignPublicKey).IsRequired().HasMaxLength(Constants.SyncConstants.SyncDeltaEd25519PublicKeyBytes);
        controlOperation.Property(operation => operation.OriginSignature).IsRequired().HasMaxLength(Constants.SyncConstants.SyncDeltaEd25519SignatureBytes);
        controlOperation.Property(operation => operation.EnvelopePayload).IsRequired().HasMaxLength(Constants.SyncConstants.MaxUserControlOperationEnvelopeBytes);
        controlOperation.Property(operation => operation.Status).HasConversion<byte>().IsRequired();
        controlOperation.Property(operation => operation.StatusReason).HasMaxLength(512);
        controlOperation.Property(operation => operation.ConflictingOperationHash).HasMaxLength(Constants.SyncConstants.SyncDeltaPayloadHashBytes);
        controlOperation.HasIndex(operation => new
        {
            operation.UserId,
            operation.OriginDeviceId,
            operation.OriginInstanceId,
            operation.OriginSequence
        }).IsUnique();
        controlOperation.HasIndex(operation => new { operation.UserId, operation.PreviousKeyEpoch })
            .IsUnique()
            .HasDatabaseName("UX_UserControlOperations_ActiveKeyBase")
            .HasFilter("OperationType = 1 AND Status IN (1, 2)");
        controlOperation.HasIndex(operation => new { operation.UserId, operation.PreviousMembershipEpoch })
            .IsUnique()
            .HasDatabaseName("UX_UserControlOperations_ActiveMembershipBase")
            .HasFilter("OperationType IN (3, 4) AND Status IN (1, 2)");
        controlOperation.HasIndex(operation => new { operation.UserId, operation.Status, operation.CreatedAtUtc });

        var controlState = model.Entity<UserControlState>();
        controlState.ToTable("UserControlStates");
        controlState.HasKey(state => state.UserId);
        controlState.Property(state => state.LocalOriginInstanceId).IsRequired();
        controlState.Property(state => state.NextOriginSequence).IsRequired().HasDefaultValue(1L);
        controlState.Property(state => state.AppliedKeyEpoch).IsRequired();
        controlState.Property(state => state.AppliedMembershipEpoch).IsRequired();
        controlState.Property(state => state.HasConflict).IsRequired();
        controlState.Property(state => state.ConflictReason).HasMaxLength(512);
        controlState.Property(state => state.ConflictingOperationHash).HasMaxLength(Constants.SyncConstants.SyncDeltaPayloadHashBytes);
        controlState.Property(state => state.LastUpdatedAtUtc).IsRequired();

        var membershipAuthorization = model.Entity<UserMembershipAuthorization>();
        membershipAuthorization.ToTable("UserMembershipAuthorizations");
        membershipAuthorization.HasKey(row => row.AuthorizationId);
        membershipAuthorization.Property(row => row.SignPublicKey).IsRequired().HasMaxLength(Constants.SyncConstants.SyncDeltaEd25519PublicKeyBytes);
        membershipAuthorization.Property(row => row.SignPublicKeyHash).IsRequired().HasMaxLength(Constants.SyncConstants.SyncDeltaPayloadHashBytes);
        membershipAuthorization.Property(row => row.AgreementPublicKeyHash).IsRequired().HasMaxLength(Constants.SyncConstants.SyncDeltaPayloadHashBytes);
        membershipAuthorization.Property(row => row.TlsCertFingerprint).IsRequired().HasMaxLength(128);
        membershipAuthorization.Property(row => row.DeviceType).HasConversion<byte>().IsRequired();
        membershipAuthorization.Property(row => row.StartedMembershipEpoch).IsRequired();
        membershipAuthorization.Property(row => row.MinimumKeyEpoch).IsRequired();
        membershipAuthorization.Property(row => row.IsActive).IsRequired();
        membershipAuthorization.Property(row => row.IsGenesis).IsRequired();
        membershipAuthorization.Property(row => row.Version).IsRequired().IsConcurrencyToken();
        membershipAuthorization.Property(row => row.AdditionOperationHash).HasMaxLength(Constants.SyncConstants.SyncDeltaPayloadHashBytes);
        membershipAuthorization.Property(row => row.RemovalOperationHash).HasMaxLength(Constants.SyncConstants.SyncDeltaPayloadHashBytes);
        membershipAuthorization.HasIndex(row => new { row.UserId, row.DeviceId, row.OriginInstanceId })
            .IsUnique()
            .HasDatabaseName("UX_UserMembershipAuthorizations_ExactInstallation");
        membershipAuthorization.HasIndex(row => new { row.UserId, row.DeviceId })
            .IsUnique()
            .HasDatabaseName("UX_UserMembershipAuthorizations_ActiveDevice")
            .HasFilter("IsActive = 1");
        membershipAuthorization.HasIndex(row => new { row.UserId, row.StartedMembershipEpoch, row.EndedMembershipEpoch });

        var removalCutoff = model.Entity<UserOriginRemovalCutoff>();
        removalCutoff.ToTable("UserOriginRemovalCutoffs");
        removalCutoff.HasKey(row => row.CutoffId);
        removalCutoff.Property(row => row.RemovalOperationHash).IsRequired().HasMaxLength(Constants.SyncConstants.SyncDeltaPayloadHashBytes);
        removalCutoff.Property(row => row.UserKeyEpoch).IsRequired();
        removalCutoff.Property(row => row.HighestAcceptedSnapshotRevision).IsRequired();
        removalCutoff.Property(row => row.HighestAcceptedControlSequence).IsRequired();
        removalCutoff.Property(row => row.ResultingMembershipEpoch).IsRequired();
        removalCutoff.HasIndex(row => new { row.UserId, row.DeviceId, row.OriginInstanceId, row.UserKeyEpoch }).IsUnique();
        removalCutoff.HasIndex(row => new { row.UserId, row.RemovalOperationId });

        var deletedUserBarrier = model.Entity<DeletedUserBarrier>();
        deletedUserBarrier.ToTable("DeletedUserBarriers");
        deletedUserBarrier.HasKey(barrier => barrier.UserId);
        deletedUserBarrier.Property(barrier => barrier.DeletionOperationId).IsRequired();
        deletedUserBarrier.Property(barrier => barrier.DeletionGeneration).IsRequired();
        deletedUserBarrier.Property(barrier => barrier.OriginDeviceId).IsRequired();
        deletedUserBarrier.Property(barrier => barrier.OriginInstanceId).IsRequired();
        deletedUserBarrier.Property(barrier => barrier.OriginSequence).IsRequired();
        deletedUserBarrier.Property(barrier => barrier.KeyEpoch).IsRequired();
        deletedUserBarrier.Property(barrier => barrier.MembershipEpoch).IsRequired();
        deletedUserBarrier.Property(barrier => barrier.DeletedAtUtc).IsRequired();
        deletedUserBarrier.Property(barrier => barrier.AppliedAtUtc).IsRequired();
        deletedUserBarrier.Property(barrier => barrier.LastUpdatedAtUtc).IsRequired();
        deletedUserBarrier.Property(barrier => barrier.OperationHash).IsRequired().HasMaxLength(Constants.SyncConstants.SyncDeltaPayloadHashBytes).IsConcurrencyToken();
        deletedUserBarrier.Property(barrier => barrier.OriginSignPublicKey).IsRequired().HasMaxLength(Constants.SyncConstants.SyncDeltaEd25519PublicKeyBytes);
        deletedUserBarrier.Property(barrier => barrier.OriginSignature).IsRequired().HasMaxLength(Constants.SyncConstants.SyncDeltaEd25519SignatureBytes);
        deletedUserBarrier.Property(barrier => barrier.HasConflict).IsRequired();
        deletedUserBarrier.Property(barrier => barrier.ConflictingOperationHash).HasMaxLength(Constants.SyncConstants.SyncDeltaPayloadHashBytes);
        deletedUserBarrier.HasIndex(barrier => barrier.DeletionOperationId).IsUnique();
        deletedUserBarrier.HasIndex(barrier => new { barrier.OriginDeviceId, barrier.OriginInstanceId, barrier.OriginSequence });

        var enrollmentCommit = model.Entity<DeviceEnrollmentCommit>();
        enrollmentCommit.ToTable("DeviceEnrollmentCommits");
        enrollmentCommit.HasKey(row => row.CommitId);
        enrollmentCommit.Property(row => row.TargetSignPublicKeyHash).IsRequired().HasMaxLength(Constants.SyncConstants.SyncDeltaPayloadHashBytes);
        enrollmentCommit.Property(row => row.TargetAgreementPublicKeyHash).IsRequired().HasMaxLength(Constants.SyncConstants.SyncDeltaPayloadHashBytes);
        enrollmentCommit.Property(row => row.TargetTlsCertFingerprint).IsRequired().HasMaxLength(128);
        enrollmentCommit.Property(row => row.TargetDeviceType).HasConversion<byte>().IsRequired();
        enrollmentCommit.Property(row => row.AdditionOperationHash).IsRequired().HasMaxLength(Constants.SyncConstants.SyncDeltaPayloadHashBytes);
        enrollmentCommit.Property(row => row.Status).HasConversion<byte>().IsRequired();
        enrollmentCommit.Property(row => row.LastError).HasMaxLength(512);
        enrollmentCommit.Property(row => row.Version).IsRequired().IsConcurrencyToken();
        enrollmentCommit.HasIndex(row => new { row.UserId, row.TargetDeviceId, row.TargetOriginInstanceId }).IsUnique();
        enrollmentCommit.HasIndex(row => new { row.UserId, row.Status, row.LastAttemptAtUtc });

        var itemClock = model.Entity<SyncVersionClockState>();
        itemClock.ToTable("SyncVersionClockState", table =>
            table.HasCheckConstraint("CK_SyncVersionClockState_Singleton", "Id = 1"));
        itemClock.HasKey(row => row.Id);
        itemClock.Property(row => row.LastPhysicalTimeUnixMilliseconds).IsRequired();
        itemClock.Property(row => row.LastLogicalCounter).IsRequired();
        itemClock.Property(row => row.LastUpdatedAtUtc).IsRequired();
        itemClock.Property(row => row.Version).IsRequired().IsConcurrencyToken();

        var ldi = model.Entity<LocalDeviceIdentity>();
        ldi.ToTable("LocalDeviceIdentity", t =>
            t.HasCheckConstraint("CK_LocalDeviceIdentity_SingletonKey", "SingletonKey = 1"));
        ldi.HasKey(x => x.Id);
        ldi.Property<int>("SingletonKey").HasDefaultValue(1).IsRequired();
        ldi.HasIndex("SingletonKey").IsUnique();
        ldi.Property(x => x.OriginInstanceId).IsRequired();
        ldi.Property(x => x.AgreementPrivateKeyBlob).IsRequired();
        ldi.Property(x => x.SignPrivateKeyBlob).IsRequired();
        ldi.Property(x => x.PFXCertificate).IsRequired();
        ldi.Property(x => x.DeviceType).HasConversion<byte>().IsRequired();
        ldi.Property(x => x.IsSyncOn).IsRequired().HasDefaultValue(false);
        ldi.Property(x => x.CreatedAt).IsRequired();

        ApplyUtcDateTimeConverters(model);
    }
}
