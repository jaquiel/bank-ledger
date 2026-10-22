using Ledger.Application;
using Ledger.Domain;
using Shouldly;
using Xunit;

namespace Ledger.UnitTests;

public sealed class IdempotencyKeyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_key_is_rejected(string? value)
    {
        IdempotencyKey.Create(value).Error!.Code.ShouldBe("idempotency_key_required");
    }

    [Theory]
    [InlineData("com espaço")]
    [InlineData("acentuação")]
    public void Key_with_non_visible_ascii_is_rejected(string value)
    {
        IdempotencyKey.Create(value).Error!.Code.ShouldBe("idempotency_key_invalid");
    }

    [Fact]
    public void Key_longer_than_limit_is_rejected()
    {
        IdempotencyKey.Create(new string('a', IdempotencyKey.MaxLength + 1)).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void Uuid_is_a_valid_key()
    {
        IdempotencyKey.Create(Guid.NewGuid().ToString()).IsSuccess.ShouldBeTrue();
    }
}

public sealed class RequestFingerprintTests
{
    private static readonly Guid AccountId = Guid.NewGuid();

    [Fact]
    public void Same_operation_produces_same_fingerprint()
    {
        var a = RequestFingerprint.ForMovement(AccountId, Movement.Create(EntryType.Credit, 10m, "x").Value);
        var b = RequestFingerprint.ForMovement(AccountId, Movement.Create(EntryType.Credit, 10.00m, "x").Value);

        a.ShouldBe(b);
        a.Length.ShouldBe(64);
    }

    [Fact]
    public void Any_difference_in_the_operation_changes_the_fingerprint()
    {
        var baseline = RequestFingerprint.ForMovement(AccountId, Movement.Create(EntryType.Credit, 10m, "x").Value);

        string[] variations =
        [
            RequestFingerprint.ForMovement(AccountId, Movement.Create(EntryType.Debit, 10m, "x").Value),
            RequestFingerprint.ForMovement(AccountId, Movement.Create(EntryType.Credit, 10.01m, "x").Value),
            RequestFingerprint.ForMovement(AccountId, Movement.Create(EntryType.Credit, 10m, "y").Value),
            RequestFingerprint.ForMovement(Guid.NewGuid(), Movement.Create(EntryType.Credit, 10m, "x").Value),
            RequestFingerprint.ForReversal(AccountId, Guid.NewGuid()),
        ];

        variations.ShouldAllBe(v => v != baseline);
    }
}

public sealed class StatementCursorTests
{
    [Theory]
    [InlineData(1L)]
    [InlineData(42L)]
    [InlineData(long.MaxValue)]
    public void Cursor_round_trips(long sequence)
    {
        StatementCursor.Decode(StatementCursor.Encode(sequence)).Value.ShouldBe(sequence);
    }

    [Theory]
    [InlineData("not-a-cursor!")]
    [InlineData("AAAAAAAAAAA")]
    [InlineData("AQ")]
    public void Invalid_cursor_is_rejected(string cursor)
    {
        StatementCursor.Decode(cursor).Error!.Code.ShouldBe("invalid_cursor");
    }

    [Fact]
    public void Cursor_is_url_safe()
    {
        StatementCursor.Encode(long.MaxValue).ShouldNotContain('+');
        StatementCursor.Encode(long.MaxValue).ShouldNotContain('/');
        StatementCursor.Encode(long.MaxValue).ShouldNotContain('=');
    }
}

public sealed class LedgerServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeWriter _writer = new();
    private readonly FakeReader _reader = new();
    private readonly LedgerService _service;

    public LedgerServiceTests()
    {
        _service = new LedgerService(_writer, _reader, new FixedTimeProvider(Now));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Balance_in_the_future_is_rejected_without_hitting_the_database()
    {
        var result = await _service.GetBalanceAsync(Guid.NewGuid(), Now.AddHours(1), Ct);

        result.Error!.Code.ShouldBe("invalid_instant");
        _reader.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Small_client_clock_skew_is_tolerated()
    {
        var result = await _service.GetBalanceAsync(Guid.NewGuid(), Now.AddSeconds(30), Ct);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Invalid_movement_never_reaches_the_writer()
    {
        var context = new OperationContext("client", null);

        var noKey = await _service.RegisterMovementAsync(Guid.NewGuid(), EntryType.Credit, 10, null, null, context, Ct);
        var badAmount = await _service.RegisterMovementAsync(Guid.NewGuid(), EntryType.Credit, -10, null, "k", context, Ct);

        noKey.Error!.Code.ShouldBe("idempotency_key_required");
        badAmount.Error!.Code.ShouldBe("invalid_amount");
        _writer.Calls.ShouldBe(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(LedgerService.MaxPageSize + 1)]
    public async Task Page_size_outside_bounds_is_rejected(int limit)
    {
        (await _service.GetStatementAsync(Guid.NewGuid(), null, null, null, limit, Ct)).Error!.Code.ShouldBe("invalid_limit");
    }

    [Fact]
    public async Task Inverted_period_is_rejected()
    {
        (await _service.GetStatementAsync(Guid.NewGuid(), Now, Now.AddDays(-1), null, null, Ct)).Error!.Code.ShouldBe("invalid_period");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10.001)]
    public async Task Invalid_overdraft_limit_is_rejected(decimal limit)
    {
        (await _service.CreateAccountAsync(limit, Ct)).Error!.Code.ShouldBe("invalid_overdraft_limit");
    }

    private sealed class FakeWriter : ILedgerWriter
    {
        public int Calls { get; private set; }

        public Task<Account> CreateAccountAsync(decimal overdraftLimit, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new Account(Guid.NewGuid(), "BRL", 0, overdraftLimit, AccountStatus.Active, 0, Now));
        }

        public Task<Result<MovementOutcome>> RegisterMovementAsync(RegisterMovementCommand command, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("Não deveria ser chamado.");
        }

        public Task<Result<MovementOutcome>> ReverseEntryAsync(ReverseEntryCommand command, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("Não deveria ser chamado.");
        }
    }

    private sealed class FakeReader : ILedgerReader
    {
        public int Calls { get; private set; }

        public Task<Account?> GetAccountAsync(Guid accountId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<Account?>(null);
        }

        public Task<BalanceSnapshot?> GetBalanceAsync(Guid accountId, DateTimeOffset? at, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<BalanceSnapshot?>(new BalanceSnapshot(accountId, "BRL", 0, at ?? Now, 0));
        }

        public Task<StatementPage?> GetStatementAsync(StatementQuery query, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<StatementPage?>(new StatementPage([], null));
        }

        public Task<LedgerEntry?> GetEntryAsync(Guid accountId, Guid entryId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<LedgerEntry?>(null);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
