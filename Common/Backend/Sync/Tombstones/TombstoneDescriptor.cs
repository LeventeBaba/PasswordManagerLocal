using Microsoft.Extensions.DependencyInjection;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using System.Text.Json;

namespace PasswordManagerLocal.Common.Backend.Sync.Tombstones;

internal sealed record TombstoneDescriptor(
    TombstoneItemType ItemType,
    Guid ItemId,
    SyncVersionStamp Version,
    TombstoneCausalReference CausalReference,
    Func<UserDataBlobKind> Remove);
