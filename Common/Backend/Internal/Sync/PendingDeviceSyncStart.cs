using Microsoft.Extensions.DependencyInjection;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Sync;
using System.Collections.Concurrent;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;

using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.Internal.Sync;

internal sealed record PendingDeviceSyncStart(DiscoveredDeviceEndpoint Endpoint, Device Device);
