-- =====================================================================================
-- V001 - Schema inicial do ledger.
-- Princípio: as invariantes críticas são garantidas PELO BANCO (constraints, índices
-- únicos, triggers), não só pelo código. Um bug na aplicação não consegue violá-las.
-- =====================================================================================

-- Contas: saldo corrente materializado, atualizado na MESMA transação de cada lançamento.
CREATE TABLE accounts (
    id              uuid          PRIMARY KEY,
    currency        char(3)       NOT NULL DEFAULT 'BRL',
    balance         numeric(19,2) NOT NULL DEFAULT 0,
    overdraft_limit numeric(19,2) NOT NULL DEFAULT 0,
    status          text          NOT NULL DEFAULT 'active',
    version         bigint        NOT NULL DEFAULT 0,
    created_at      timestamptz   NOT NULL DEFAULT clock_timestamp(),
    updated_at      timestamptz   NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT ck_accounts_status          CHECK (status IN ('active', 'blocked')),
    CONSTRAINT ck_accounts_overdraft_limit CHECK (overdraft_limit >= 0),
    -- Defesa em profundidade: mesmo com bug na aplicação, o saldo nunca ultrapassa o limite.
    CONSTRAINT ck_accounts_balance_within_limit CHECK (balance >= -overdraft_limit)
);

-- Livro-razão: append-only. Cada linha guarda o saldo resultante (balance_after), o que torna
-- "saldo no instante T" uma única leitura indexada, sem somatórios.
CREATE TABLE ledger_entries (
    id             uuid          PRIMARY KEY,
    account_id     uuid          NOT NULL REFERENCES accounts (id),
    sequence       bigint        NOT NULL,
    type           text          NOT NULL,
    amount         numeric(19,2) NOT NULL,
    balance_after  numeric(19,2) NOT NULL,
    description    varchar(140),
    reversal_of    uuid          REFERENCES ledger_entries (id),
    occurred_at    timestamptz   NOT NULL,
    created_by     text          NOT NULL,
    correlation_id text,
    CONSTRAINT ck_ledger_entries_type     CHECK (type IN ('credit', 'debit')),
    CONSTRAINT ck_ledger_entries_amount   CHECK (amount > 0),
    CONSTRAINT ck_ledger_entries_sequence CHECK (sequence > 0),
    -- Ordem total por conta: dois lançamentos nunca ocupam a mesma posição.
    CONSTRAINT uq_ledger_entries_account_sequence UNIQUE (account_id, sequence)
);

-- Suporta "saldo em T" (último lançamento com occurred_at <= T).
CREATE INDEX ix_ledger_entries_account_occurred
    ON ledger_entries (account_id, occurred_at DESC, sequence DESC);

-- Um lançamento só pode ser estornado uma vez.
CREATE UNIQUE INDEX uq_ledger_entries_reversal_of
    ON ledger_entries (reversal_of)
    WHERE reversal_of IS NOT NULL;

-- Imutabilidade garantida no banco. Correções são feitas com estorno (lançamento compensatório).
-- Em produção, soma-se a isso o REVOKE UPDATE/DELETE/TRUNCATE da role da aplicação.
CREATE FUNCTION ledger_entries_reject_mutation() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'ledger_entries é append-only: operação % não é permitida', TG_OP
        USING ERRCODE = 'restrict_violation',
              HINT = 'Corrija lançamentos com um estorno.';
END;
$$;

CREATE TRIGGER trg_ledger_entries_no_update_delete
    BEFORE UPDATE OR DELETE ON ledger_entries
    FOR EACH ROW EXECUTE FUNCTION ledger_entries_reject_mutation();

CREATE TRIGGER trg_ledger_entries_no_truncate
    BEFORE TRUNCATE ON ledger_entries
    FOR EACH STATEMENT EXECUTE FUNCTION ledger_entries_reject_mutation();

-- Idempotência: a chave é reservada na mesma transação do lançamento.
-- Requisições concorrentes com a mesma chave esperam no índice único e, depois, fazem replay.
CREATE TABLE idempotency_keys (
    client_id    text        NOT NULL,
    key          text        NOT NULL,
    request_hash char(64)    NOT NULL,
    entry_id     uuid        REFERENCES ledger_entries (id),
    created_at   timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (client_id, key)
);

-- Transactional outbox: eventos gravados na mesma transação do lançamento (sem dual-write).
CREATE TABLE outbox_messages (
    id           uuid        PRIMARY KEY,
    type         text        NOT NULL,
    payload      jsonb       NOT NULL,
    occurred_at  timestamptz NOT NULL,
    published_at timestamptz,
    attempts     integer     NOT NULL DEFAULT 0,
    last_error   text
);

CREATE INDEX ix_outbox_messages_pending
    ON outbox_messages (occurred_at)
    WHERE published_at IS NULL;
