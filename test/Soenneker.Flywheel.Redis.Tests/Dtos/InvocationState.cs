using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Flywheel.Core;
using StackExchange.Redis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soenneker.Flywheel.Generated;

namespace Soenneker.Flywheel.Redis.Tests;

public sealed class InvocationState { public string? Value; public bool Disposed; }
