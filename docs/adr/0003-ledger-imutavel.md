# ADR 0003 — Ledger imutável (append-only) com `balance_after` e saldo materializado

**Status:** aceita

## Contexto
Dados financeiros precisam ser auditáveis: todo saldo deve ser explicável pelo histórico, e nenhuma correção pode apagar o que aconteceu. Também é preciso responder o saldo em qualquer instante, rapidamente, mesmo para contas com histórico longo.

## Decisão
- `ledger_entries` é **append-only**. Triggers no banco rejeitam `UPDATE`, `DELETE` e `TRUNCATE`.
- Correções são feitas com **estorno**: um novo lançamento do tipo oposto, com `reversal_of` apontando o original. Um índice único parcial garante no máximo um estorno por lançamento; estorno de estorno é recusado.
- Cada lançamento guarda **`sequence`** (ordem total por conta, sem lacunas) e **`balance_after`** (saldo resultante).
- `accounts.balance` é o saldo corrente **materializado na mesma transação** de cada lançamento.
- Valores sempre positivos em `amount`; o sinal vem do `type`.

## Alternativas consideradas
- **Calcular saldo somando lançamentos:** custo cresce com o histórico; inviável em pico para contas antigas.
- **Snapshots periódicos + soma do delta:** resolve custo, mas acrescenta um processo batch e casos de borda. `balance_after` dá o mesmo resultado com uma leitura indexada e sem processo extra.
- **Permitir edição com tabela de auditoria:** a auditoria vira um segundo registro que pode divergir do primeiro.

## Consequências
- (+) Saldo em T é uma única busca no índice `(account_id, occurred_at DESC, sequence DESC)`.
- (+) A cadeia `sequence`/`balance_after` é autoverificável (os testes de concorrência checam que cada lançamento parte exatamente do saldo deixado pelo anterior).
- (−) **Lançamentos retroativos não são suportados.** A posição é definida pelo horário de registro (`occurred_at`, atribuído pelo servidor). Aceitar data de efetivação no passado exigiria um modelo bitemporal (data de efetivação × data de registro) e o recálculo de `balance_after` dos lançamentos posteriores, o que conflita com a imutabilidade. É a primeira evolução de modelo que eu faria com mais tempo.
- (−) Estorno de um crédito já gasto é recusado por saldo insuficiente (decisão de negócio explícita; a alternativa seria permitir saldo negativo em estornos, o que deve ser decidido com a área de negócio).
