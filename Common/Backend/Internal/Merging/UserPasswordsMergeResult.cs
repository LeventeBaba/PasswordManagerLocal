using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.Internal.Merging;

internal sealed record UserPasswordsMergeResult<TLive, TDeleted>(List<TLive> Live, List<TDeleted> Deleted);
