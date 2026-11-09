-- Initial vault schema. The recovery journal is installed by the additive startup upgrade.

CREATE TABLE "Users" (
    "UId" TEXT NOT NULL,
    "EncryptedGeneralUserDataPayload" BLOB NOT NULL,
    "EncryptedPayload" BLOB NOT NULL,
    "EncryptedUserDevicesDataPayload" BLOB NOT NULL,
    "EncryptedUserPasswordsDataPayload" BLOB NOT NULL,
    "GeneralDataVersionLogicalCounter" INTEGER NOT NULL,
    "GeneralDataVersionOriginDeviceId" TEXT NOT NULL,
    "GeneralDataVersionOriginInstanceId" TEXT NOT NULL,
    "GeneralDataVersionPhysicalTimeUnixMilliseconds" INTEGER NOT NULL,
    "GeneralUserDataLastModifiedAt" TEXT NOT NULL,
    "IntegrityHash" BLOB NOT NULL,
    "KeyEpoch" INTEGER NOT NULL,
    "LastModifiedAt" TEXT NOT NULL,
    "MembershipEpoch" INTEGER NOT NULL,
    "PasswordSalt" BLOB NOT NULL,
    "SavedKey" BLOB NULL,
    "UserDataLastModifiedAt" TEXT NOT NULL,
    "UserDevicesDataLastModifiedAt" TEXT NOT NULL,
    "UserPasswordsDataLastModifiedAt" TEXT NOT NULL,
    "UsernameHash" BLOB NOT NULL,
    "UsernameSalt" BLOB NOT NULL,
    CONSTRAINT "PK_Users" PRIMARY KEY ("UId")
);

CREATE TABLE "Devices" (
    "Id" TEXT NOT NULL,
    "BlockedAt" TEXT NULL,
    "BlockedReason" TEXT NULL,
    "DeviceType" INTEGER NOT NULL,
    "IntegrityHash" BLOB NOT NULL,
    "InvalidSyncAttemptCount" INTEGER NOT NULL,
    "IsBlocked" INTEGER NOT NULL,
    "IsTrusted" INTEGER NOT NULL,
    "LastInvalidSyncAttemptAt" TEXT NULL,
    "LastKnownHash" BLOB NOT NULL,
    "LastModifiedAt" TEXT NOT NULL,
    "LastSeen" TEXT NOT NULL,
    "LastSync" TEXT NOT NULL,
    "PublicKey" BLOB NOT NULL,
    "SignPublicKey" BLOB NOT NULL,
    "SignPublicKeyHash" BLOB NOT NULL,
    "TlsCertFingerprint" TEXT NOT NULL,
    CONSTRAINT "PK_Devices" PRIMARY KEY ("Id")
);

CREATE TABLE "LocalDeviceIdentity" (
    "Id" TEXT NOT NULL,
    "AgreementPrivateKeyBlob" BLOB NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "DeviceType" INTEGER NOT NULL,
    "IntegrityHash" BLOB NOT NULL,
    "IsSyncOn" INTEGER NOT NULL DEFAULT 0,
    "OriginInstanceId" TEXT NOT NULL,
    "PFXCertificate" BLOB NOT NULL,
    "SignPrivateKeyBlob" BLOB NOT NULL,
    "SingletonKey" INTEGER NOT NULL DEFAULT 1,
    CONSTRAINT "PK_LocalDeviceIdentity" PRIMARY KEY ("Id"),
    CONSTRAINT "CK_LocalDeviceIdentity_SingletonKey" CHECK (SingletonKey = 1)
);

CREATE TABLE "SyncItems" (
    "Id" TEXT NOT NULL,
    "ChangeType" INTEGER NOT NULL,
    "ChangedAtTs" INTEGER NOT NULL,
    "ModelId" TEXT NOT NULL,
    "ModelType" INTEGER NOT NULL,
    CONSTRAINT "PK_SyncItems" PRIMARY KEY ("Id")
);

CREATE TABLE "UserDevices" (
    "UserId" TEXT NOT NULL,
    "DeviceId" TEXT NOT NULL,
    "DeletedAt" TEXT NULL,
    "IntegrityHash" BLOB NOT NULL,
    "IsDeleted" INTEGER NOT NULL,
    "IsSyncOn" INTEGER NOT NULL,
    "LastModifiedAt" TEXT NOT NULL,
    "ModelId" TEXT NOT NULL,
    CONSTRAINT "PK_UserDevices" PRIMARY KEY ("UserId", "DeviceId"),
    CONSTRAINT "FK_UserDevices_Devices_DeviceId" FOREIGN KEY ("DeviceId") REFERENCES "Devices" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_UserDevices_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("UId") ON DELETE CASCADE
);

CREATE TABLE "LocalUserDevices" (
    "UserId" TEXT NOT NULL,
    "LocalDeviceIdentityId" TEXT NOT NULL,
    "IntegrityHash" BLOB NOT NULL,
    "IsSyncOn" INTEGER NOT NULL DEFAULT 1,
    CONSTRAINT "PK_LocalUserDevices" PRIMARY KEY ("UserId", "LocalDeviceIdentityId"),
    CONSTRAINT "FK_LocalUserDevices_LocalDeviceIdentity_LocalDeviceIdentityId" FOREIGN KEY ("LocalDeviceIdentityId") REFERENCES "LocalDeviceIdentity" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_LocalUserDevices_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("UId") ON DELETE CASCADE
);

CREATE TABLE "SyncQueueItems" (
    "QueueId" INTEGER NOT NULL CONSTRAINT "PK_SyncQueueItems" PRIMARY KEY AUTOINCREMENT,
    "DeviceId" TEXT NOT NULL,
    "EnqueuedAt" TEXT NOT NULL,
    "Id" TEXT NOT NULL,
    "ProcessedAt" TEXT NULL,
    "SyncItemId" TEXT NOT NULL,
    CONSTRAINT "AK_SyncQueueItems_Id" UNIQUE ("Id"),
    CONSTRAINT "FK_SyncQueueItems_Devices_DeviceId" FOREIGN KEY ("DeviceId") REFERENCES "Devices" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_SyncQueueItems_SyncItems_SyncItemId" FOREIGN KEY ("SyncItemId") REFERENCES "SyncItems" ("Id") ON DELETE CASCADE
);

CREATE TABLE "SyncTombstones" (
    "Id" TEXT NOT NULL,
    "DeletedAtTs" INTEGER NOT NULL,
    "ModelId" TEXT NOT NULL,
    "ModelType" INTEGER NOT NULL,
    CONSTRAINT "PK_SyncTombstones" PRIMARY KEY ("Id")
);

CREATE TABLE "SyncVersionClockState" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_SyncVersionClockState" PRIMARY KEY AUTOINCREMENT,
    "LastLogicalCounter" INTEGER NOT NULL,
    "LastPhysicalTimeUnixMilliseconds" INTEGER NOT NULL,
    "LastUpdatedAtUtc" TEXT NOT NULL,
    "Version" INTEGER NOT NULL,
    CONSTRAINT "CK_SyncVersionClockState_Singleton" CHECK (Id = 1)
);

CREATE TABLE "UserSyncSnapshots" (
    "Id" TEXT NOT NULL,
    "ConflictingSnapshotHash" BLOB NULL,
    "CreatedAtUtc" TEXT NOT NULL,
    "EnvelopePayload" BLOB NOT NULL,
    "LastReceivedFromDeviceId" TEXT NULL,
    "MembershipEpoch" INTEGER NOT NULL,
    "OriginDeviceId" TEXT NOT NULL,
    "OriginInstanceId" TEXT NOT NULL,
    "OriginRevision" INTEGER NOT NULL,
    "OriginSignPublicKey" BLOB NOT NULL,
    "OriginSignature" BLOB NOT NULL,
    "QuarantineReason" TEXT NULL,
    "ReceivedAtUtc" TEXT NOT NULL,
    "SnapshotHash" BLOB NOT NULL,
    "Status" INTEGER NOT NULL,
    "UserId" TEXT NOT NULL,
    "UserKeyEpoch" INTEGER NOT NULL,
    CONSTRAINT "PK_UserSyncSnapshots" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_UserSyncSnapshots_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("UId") ON DELETE CASCADE
);

CREATE TABLE "UserSyncStates" (
    "UserId" TEXT NOT NULL,
    "LastPublishedContentHash" BLOB NOT NULL,
    "LastUpdatedAtUtc" TEXT NOT NULL,
    "LocalOriginInstanceId" TEXT NOT NULL,
    "NextOriginRevision" INTEGER NOT NULL DEFAULT 1,
    CONSTRAINT "PK_UserSyncStates" PRIMARY KEY ("UserId"),
    CONSTRAINT "FK_UserSyncStates_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("UId") ON DELETE CASCADE
);

CREATE TABLE "UserRevisionKnowledge" (
    "UserId" TEXT NOT NULL,
    "OriginDeviceId" TEXT NOT NULL,
    "OriginInstanceId" TEXT NOT NULL,
    "UserKeyEpoch" INTEGER NOT NULL,
    "HighestMergedRevision" INTEGER NOT NULL,
    "HighestStoredRevision" INTEGER NOT NULL,
    "HighestStoredSnapshotHash" BLOB NOT NULL,
    "LastUpdatedAtUtc" TEXT NOT NULL,
    CONSTRAINT "PK_UserRevisionKnowledge" PRIMARY KEY ("UserId", "OriginDeviceId", "OriginInstanceId", "UserKeyEpoch"),
    CONSTRAINT "FK_UserRevisionKnowledge_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("UId") ON DELETE CASCADE
);

CREATE TABLE "UserControlOperations" (
    "OperationId" TEXT NOT NULL,
    "AppliedAtUtc" TEXT NULL,
    "ConflictingOperationHash" BLOB NULL,
    "CreatedAtUtc" TEXT NOT NULL,
    "EnvelopePayload" BLOB NOT NULL,
    "LastReceivedFromDeviceId" TEXT NULL,
    "OperationHash" BLOB NOT NULL,
    "OperationType" INTEGER NOT NULL,
    "OriginDeviceId" TEXT NOT NULL,
    "OriginInstanceId" TEXT NOT NULL,
    "OriginSequence" INTEGER NOT NULL,
    "OriginSignPublicKey" BLOB NOT NULL,
    "OriginSignature" BLOB NOT NULL,
    "PayloadHash" BLOB NOT NULL,
    "PreviousKeyEpoch" INTEGER NOT NULL,
    "PreviousMembershipEpoch" INTEGER NOT NULL,
    "ReceivedAtUtc" TEXT NOT NULL,
    "ResultingKeyEpoch" INTEGER NOT NULL,
    "ResultingMembershipEpoch" INTEGER NOT NULL,
    "Status" INTEGER NOT NULL,
    "StatusReason" TEXT NULL,
    "UserId" TEXT NOT NULL,
    CONSTRAINT "PK_UserControlOperations" PRIMARY KEY ("OperationId")
);

CREATE TABLE "UserControlStates" (
    "UserId" TEXT NOT NULL,
    "AppliedKeyEpoch" INTEGER NOT NULL,
    "AppliedMembershipEpoch" INTEGER NOT NULL,
    "ConflictReason" TEXT NULL,
    "ConflictingOperationHash" BLOB NULL,
    "ConflictingOperationId" TEXT NULL,
    "HasConflict" INTEGER NOT NULL,
    "LastUpdatedAtUtc" TEXT NOT NULL,
    "LocalOriginInstanceId" TEXT NOT NULL,
    "NextOriginSequence" INTEGER NOT NULL DEFAULT 1,
    CONSTRAINT "PK_UserControlStates" PRIMARY KEY ("UserId")
);

CREATE TABLE "UserMembershipAuthorizations" (
    "AuthorizationId" TEXT NOT NULL,
    "AdditionOperationHash" BLOB NULL,
    "AdditionOperationId" TEXT NULL,
    "AgreementPublicKeyHash" BLOB NOT NULL,
    "CreatedAtUtc" TEXT NOT NULL,
    "DeviceId" TEXT NOT NULL,
    "DeviceType" INTEGER NOT NULL,
    "EndedAtUtc" TEXT NULL,
    "EndedMembershipEpoch" INTEGER NULL,
    "IsActive" INTEGER NOT NULL,
    "IsGenesis" INTEGER NOT NULL,
    "MaximumKeyEpoch" INTEGER NULL,
    "MinimumKeyEpoch" INTEGER NOT NULL,
    "OriginInstanceId" TEXT NOT NULL,
    "RemovalOperationHash" BLOB NULL,
    "RemovalOperationId" TEXT NULL,
    "SignPublicKey" BLOB NOT NULL,
    "SignPublicKeyHash" BLOB NOT NULL,
    "StartedMembershipEpoch" INTEGER NOT NULL,
    "TlsCertFingerprint" TEXT NOT NULL,
    "UserId" TEXT NOT NULL,
    "Version" INTEGER NOT NULL,
    CONSTRAINT "PK_UserMembershipAuthorizations" PRIMARY KEY ("AuthorizationId")
);

CREATE TABLE "UserOriginRemovalCutoffs" (
    "CutoffId" TEXT NOT NULL,
    "AuthorizationId" TEXT NOT NULL,
    "CreatedAtUtc" TEXT NOT NULL,
    "DeviceId" TEXT NOT NULL,
    "HighestAcceptedControlSequence" INTEGER NOT NULL,
    "HighestAcceptedSnapshotRevision" INTEGER NOT NULL,
    "OriginInstanceId" TEXT NOT NULL,
    "RemovalOperationHash" BLOB NOT NULL,
    "RemovalOperationId" TEXT NOT NULL,
    "ResultingMembershipEpoch" INTEGER NOT NULL,
    "UserId" TEXT NOT NULL,
    "UserKeyEpoch" INTEGER NOT NULL,
    CONSTRAINT "PK_UserOriginRemovalCutoffs" PRIMARY KEY ("CutoffId")
);

CREATE TABLE "DeviceEnrollmentCommits" (
    "CommitId" TEXT NOT NULL,
    "AdditionOperationHash" BLOB NOT NULL,
    "AdditionOperationId" TEXT NOT NULL,
    "CompletedAtUtc" TEXT NULL,
    "CreatedAtUtc" TEXT NOT NULL,
    "LastAttemptAtUtc" TEXT NOT NULL,
    "LastError" TEXT NULL,
    "Status" INTEGER NOT NULL,
    "TargetAgreementPublicKeyHash" BLOB NOT NULL,
    "TargetDeviceId" TEXT NOT NULL,
    "TargetDeviceType" INTEGER NOT NULL,
    "TargetOriginInstanceId" TEXT NOT NULL,
    "TargetSignPublicKeyHash" BLOB NOT NULL,
    "TargetTlsCertFingerprint" TEXT NOT NULL,
    "UserId" TEXT NOT NULL,
    "Version" INTEGER NOT NULL,
    CONSTRAINT "PK_DeviceEnrollmentCommits" PRIMARY KEY ("CommitId")
);

CREATE TABLE "DeletedUserBarriers" (
    "UserId" TEXT NOT NULL,
    "AppliedAtUtc" TEXT NOT NULL,
    "ConflictingOperationHash" BLOB NULL,
    "ConflictingOperationId" TEXT NULL,
    "DeletedAtUtc" TEXT NOT NULL,
    "DeletionGeneration" TEXT NOT NULL,
    "DeletionOperationId" TEXT NOT NULL,
    "HasConflict" INTEGER NOT NULL,
    "KeyEpoch" INTEGER NOT NULL,
    "LastUpdatedAtUtc" TEXT NOT NULL,
    "MembershipEpoch" INTEGER NOT NULL,
    "OperationHash" BLOB NOT NULL,
    "OriginDeviceId" TEXT NOT NULL,
    "OriginInstanceId" TEXT NOT NULL,
    "OriginSequence" INTEGER NOT NULL,
    "OriginSignPublicKey" BLOB NOT NULL,
    "OriginSignature" BLOB NOT NULL,
    CONSTRAINT "PK_DeletedUserBarriers" PRIMARY KEY ("UserId")
);

CREATE TABLE "UserLoginIdentityStates" (
    "UserId" TEXT NOT NULL,
    "ConcurrencyVersion" INTEGER NOT NULL,
    "KeyEpoch" INTEGER NOT NULL,
    "MembershipEpoch" INTEGER NOT NULL,
    "SourceOriginDeviceId" TEXT NOT NULL,
    "SourceOriginInstanceId" TEXT NOT NULL,
    "SourceOriginRevision" INTEGER NOT NULL,
    "SourceSnapshotHash" BLOB NOT NULL,
    "Status" INTEGER NOT NULL,
    "StatusReason" TEXT NULL,
    "UpdatedAtUtc" TEXT NOT NULL,
    "UsernameHash" BLOB NOT NULL,
    "UsernameSalt" BLOB NOT NULL,
    "VersionLogicalCounter" INTEGER NOT NULL,
    "VersionOriginDeviceId" TEXT NOT NULL,
    "VersionOriginInstanceId" TEXT NOT NULL,
    "VersionPhysicalTimeUnixMilliseconds" INTEGER NOT NULL,
    CONSTRAINT "PK_UserLoginIdentityStates" PRIMARY KEY ("UserId"),
    CONSTRAINT "FK_UserLoginIdentityStates_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("UId") ON DELETE CASCADE
);

CREATE TABLE "UserCanonicalCheckpoints" (
    "UserId" TEXT NOT NULL,
    "CanonicalContentHash" BLOB NOT NULL,
    "CheckpointSequence" INTEGER NOT NULL,
    "CreatedAtUtc" TEXT NOT NULL,
    "KeyEpoch" INTEGER NOT NULL,
    "LocalDeviceId" TEXT NOT NULL,
    "LocalOriginInstanceId" TEXT NOT NULL,
    "MembershipEpoch" INTEGER NOT NULL,
    "SignPublicKey" BLOB NOT NULL,
    "Signature" BLOB NOT NULL,
    "UserIntegrityHash" BLOB NOT NULL,
    CONSTRAINT "PK_UserCanonicalCheckpoints" PRIMARY KEY ("UserId"),
    CONSTRAINT "FK_UserCanonicalCheckpoints_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("UId") ON DELETE CASCADE
);

CREATE TABLE "UserSyncFaults" (
    "Id" TEXT NOT NULL,
    "AffectedComponent" TEXT NOT NULL,
    "BlocksGarbageCollection" INTEGER NOT NULL,
    "BlocksLifecycle" INTEGER NOT NULL,
    "BlocksLogin" INTEGER NOT NULL,
    "BlocksMerge" INTEGER NOT NULL,
    "BlocksPublishing" INTEGER NOT NULL,
    "ConflictingHash" BLOB NOT NULL,
    "DiagnosticCode" TEXT NOT NULL,
    "ExpectedHash" BLOB NOT NULL,
    "FirstDetectedAtUtc" TEXT NOT NULL,
    "KeyEpoch" INTEGER NULL,
    "Kind" INTEGER NOT NULL,
    "LastDetectedAtUtc" TEXT NOT NULL,
    "LastRecoveryAttemptAtUtc" TEXT NULL,
    "MembershipEpoch" INTEGER NULL,
    "NextRecoveryAttemptAtUtc" TEXT NULL,
    "ObservedHash" BLOB NOT NULL,
    "OriginDeviceId" TEXT NULL,
    "OriginInstanceId" TEXT NULL,
    "OriginRevision" INTEGER NULL,
    "RecoveredAtUtc" TEXT NULL,
    "RecoveryAttemptCount" INTEGER NOT NULL,
    "Scope" INTEGER NOT NULL,
    "Status" INTEGER NOT NULL,
    "SupersedingRevision" INTEGER NULL,
    "UserId" TEXT NOT NULL,
    CONSTRAINT "PK_UserSyncFaults" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_UserSyncFaults_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("UId") ON DELETE CASCADE
);

CREATE INDEX "IX_Devices_SignPublicKeyHash" ON "Devices" ("SignPublicKeyHash");

CREATE UNIQUE INDEX "IX_Devices_TlsCertFingerprint" ON "Devices" ("TlsCertFingerprint");


CREATE UNIQUE INDEX "IX_LocalDeviceIdentity_SingletonKey" ON "LocalDeviceIdentity" ("SingletonKey");

CREATE INDEX "IX_LocalUserDevices_IsSyncOn" ON "LocalUserDevices" ("IsSyncOn");

CREATE INDEX "IX_LocalUserDevices_LocalDeviceIdentityId" ON "LocalUserDevices" ("LocalDeviceIdentityId");

CREATE UNIQUE INDEX "IX_LocalUserDevices_UserId" ON "LocalUserDevices" ("UserId");

CREATE UNIQUE INDEX "IX_SyncItems_ModelId_ModelType" ON "SyncItems" ("ModelId", "ModelType");

CREATE INDEX "IX_SyncQueueItems_DeviceId_ProcessedAt_QueueId" ON "SyncQueueItems" ("DeviceId", "ProcessedAt", "QueueId");

CREATE UNIQUE INDEX "IX_SyncQueueItems_DeviceId_SyncItemId" ON "SyncQueueItems" ("DeviceId", "SyncItemId");

CREATE INDEX "IX_SyncQueueItems_ProcessedAt_QueueId" ON "SyncQueueItems" ("ProcessedAt", "QueueId");

CREATE INDEX "IX_SyncQueueItems_SyncItemId" ON "SyncQueueItems" ("SyncItemId");

CREATE UNIQUE INDEX "IX_SyncTombstones_ModelId_ModelType" ON "SyncTombstones" ("ModelId", "ModelType");

CREATE INDEX "IX_UserDevices_DeviceId" ON "UserDevices" ("DeviceId");

CREATE INDEX "IX_UserDevices_DeviceId_IsDeleted" ON "UserDevices" ("DeviceId", "IsDeleted");

CREATE UNIQUE INDEX "IX_UserDevices_ModelId" ON "UserDevices" ("ModelId");

CREATE INDEX "IX_UserDevices_UserId_IsDeleted_IsSyncOn" ON "UserDevices" ("UserId", "IsDeleted", "IsSyncOn");

CREATE UNIQUE INDEX "IX_UserSyncSnapshots_UserId_OriginDeviceId_OriginInstanceId_UserKeyEpoch" ON "UserSyncSnapshots" ("UserId", "OriginDeviceId", "OriginInstanceId", "UserKeyEpoch");

CREATE INDEX "IX_UserSyncSnapshots_UserId_Status_ReceivedAtUtc" ON "UserSyncSnapshots" ("UserId", "Status", "ReceivedAtUtc");

CREATE INDEX "IX_UserRevisionKnowledge_UserId_HighestMergedRevision" ON "UserRevisionKnowledge" ("UserId", "HighestMergedRevision");

CREATE INDEX "IX_UserRevisionKnowledge_UserId_HighestStoredRevision" ON "UserRevisionKnowledge" ("UserId", "HighestStoredRevision");

CREATE UNIQUE INDEX "IX_UserControlOperations_UserId_OriginDeviceId_OriginInstanceId_OriginSequence" ON "UserControlOperations" ("UserId", "OriginDeviceId", "OriginInstanceId", "OriginSequence");

CREATE INDEX "IX_UserControlOperations_UserId_Status_CreatedAtUtc" ON "UserControlOperations" ("UserId", "Status", "CreatedAtUtc");

CREATE UNIQUE INDEX "UX_UserControlOperations_ActiveKeyBase" ON "UserControlOperations" ("UserId", "PreviousKeyEpoch") WHERE OperationType = 1 AND Status IN (1, 2);

CREATE UNIQUE INDEX "UX_UserControlOperations_ActiveMembershipBase" ON "UserControlOperations" ("UserId", "PreviousMembershipEpoch") WHERE OperationType IN (3, 4) AND Status IN (1, 2);

CREATE INDEX "IX_UserMembershipAuthorizations_UserId_StartedMembershipEpoch_EndedMembershipEpoch" ON "UserMembershipAuthorizations" ("UserId", "StartedMembershipEpoch", "EndedMembershipEpoch");

CREATE UNIQUE INDEX "UX_UserMembershipAuthorizations_ExactInstallation" ON "UserMembershipAuthorizations" ("UserId", "DeviceId", "OriginInstanceId");

CREATE UNIQUE INDEX "UX_UserMembershipAuthorizations_ActiveDevice" ON "UserMembershipAuthorizations" ("UserId", "DeviceId") WHERE IsActive = 1;

CREATE UNIQUE INDEX "IX_UserOriginRemovalCutoffs_UserId_DeviceId_OriginInstanceId_UserKeyEpoch" ON "UserOriginRemovalCutoffs" ("UserId", "DeviceId", "OriginInstanceId", "UserKeyEpoch");

CREATE INDEX "IX_UserOriginRemovalCutoffs_UserId_RemovalOperationId" ON "UserOriginRemovalCutoffs" ("UserId", "RemovalOperationId");

CREATE UNIQUE INDEX "IX_DeviceEnrollmentCommits_UserId_TargetDeviceId_TargetOriginInstanceId" ON "DeviceEnrollmentCommits" ("UserId", "TargetDeviceId", "TargetOriginInstanceId");

CREATE INDEX "IX_DeviceEnrollmentCommits_UserId_Status_LastAttemptAtUtc" ON "DeviceEnrollmentCommits" ("UserId", "Status", "LastAttemptAtUtc");

CREATE UNIQUE INDEX "IX_DeletedUserBarriers_DeletionOperationId" ON "DeletedUserBarriers" ("DeletionOperationId");

CREATE INDEX "IX_DeletedUserBarriers_OriginDeviceId_OriginInstanceId_OriginSequence" ON "DeletedUserBarriers" ("OriginDeviceId", "OriginInstanceId", "OriginSequence");

CREATE INDEX "IX_UserLoginIdentityStates_Status" ON "UserLoginIdentityStates" ("Status");

CREATE INDEX "IX_UserLoginIdentityStates_KeyEpoch_MembershipEpoch" ON "UserLoginIdentityStates" ("KeyEpoch", "MembershipEpoch");

CREATE INDEX "IX_UserSyncFaults_UserId_Status_Scope" ON "UserSyncFaults" ("UserId", "Status", "Scope");

CREATE INDEX "IX_UserSyncFaults_UserId_Scope_Kind_OriginDeviceId_OriginInstanceId_KeyEpoch" ON "UserSyncFaults" ("UserId", "Scope", "Kind", "OriginDeviceId", "OriginInstanceId", "KeyEpoch");
