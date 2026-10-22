# Bank Ledger — movimentações e posição consolidada

Serviço que registra créditos e débitos em contas de clientes e responde, sob demanda, o **saldo atual** ou a **posição consolidada em qualquer instante passado**, com consistência forte, idempotência e comportamento previsível sob alta demanda e falhas parciais.

> **Em uma frase:** um livro-razão *append-only* no PostgreSQL, em que cada lançamento é gravado por **uma única transação** que reserva a chave de idempotência, aplica a regra de saldo com um **UPDATE condicional atômico** (lock só na linha da conta) e registra o evento num **outbox**, tudo ou nada.

---

## Sumário

- [Como rodar](#como-rodar)
- [Como testar](#como-testar)
- [Usando a API](#usando-a-api)
- [Decisões principais](#decisões-principais)
- [Estrutura do repositório](#estrutura-do-repositório)
- [O que eu faria com mais tempo](#o-que-eu-faria-com-mais-tempo)

Documentação de apoio:
- [Arquitetura](docs/architecture.md): visão geral, fluxos de escrita/leitura, modelo de dados, modelo de consistência
- [Modos de falha](docs/failure-modes.md): o que acontece em cada cenário de falha e por quê
- [ADRs](docs/adr/): cada decisão relevante, com alternativas consideradas e consequências

---

## Como rodar

### Opção 1 — Docker Compose (recomendada)

Pré-requisito: Docker.

```bash
docker compose up --build
```

- API: http://localhost:8080
- Documentação interativa (Scalar): http://localhost:8080/scalar
- OpenAPI: http://localhost:8080/openapi/v1.json

As migrations são aplicadas automaticamente na subida.

### Opção 2 — `dotnet run` com Postgres no Docker

Pré-requisitos: [.NET SDK 10](https://dotnet.microsoft.com/download) e Docker.

```bash
docker compose up -d postgres
dotnet run --project src/Ledger.Api --launch-profile http
```

A connection string de desenvolvimento está em `src/Ledger.Api/appsettings.Development.json` e aponta para `localhost:5432`.

---

## Como testar

```bash
dotnet test
```

| Projeto | O que cobre | Dependências |
|---|---|---|
| `Ledger.UnitTests` | Regras de domínio (`Money`, `Movement`), idempotência, fingerprint, cursor, validações do caso de uso | Nenhuma |
| `Ledger.IntegrationTests` | API inteira (pipeline HTTP real) contra **PostgreSQL real**: concorrência, idempotência concorrente, saldo em T, estornos, imutabilidade, autenticação/autorização, rate limiting, banco indisponível | **Docker** (Testcontainers sobe o Postgres sozinho) |

Os testes de integração **não usam banco em memória** de propósito: concorrência, locks, constraints e triggers só existem no banco real.

Sem Docker, aponte os testes de integração para um Postgres existente (banco vazio ou criado por este projeto):

```bash
export LEDGER_TEST_CONNECTION_STRING="Host=localhost;Database=ledger_tests;Username=ledger;Password=ledger_dev_only"
dotnet test
```

**Testes que mais importam** (em `tests/Ledger.IntegrationTests`):
- `ConcurrencyTests`: 100 débitos paralelos na mesma conta nunca a deixam negativa, e a cadeia `sequence`/`balance_after` fica sem lacunas nem atualizações perdidas. Validado por teste de mutação: trocando o UPDATE atômico por "ler saldo → calcular → gravar", os três testes falham.
- `IdempotencyTests`: 25 requisições simultâneas com a mesma chave geram exatamente um lançamento.
- `ReversalTests`: 15 estornos simultâneos do mesmo lançamento são aplicados uma única vez.
- `DatabaseInvariantTests`: o próprio banco rejeita UPDATE/DELETE no ledger e saldo abaixo do limite, mesmo sem passar pela API.
- `DatabaseOutageTests`: com o banco fora do ar, escritas falham rápido com `503` + `Retry-After` e o readiness fica *unhealthy*.

O build trata **todo warning como erro** (`Directory.Build.props`), com analisadores .NET no modo *Recommended*. O CI (`.github/workflows/ci.yml`) compila em Release, roda os testes e constrói a imagem Docker.

---

## Usando a API

O arquivo [`requests.http`](requests.http) tem o roteiro completo, pronto para executar no VS Code (REST Client), Rider ou Visual Studio. Ele cobre token, conta, crédito, replay idempotente, chave reutilizada, saldo insuficiente, saldo em T, estorno e extrato.

### Autenticação

JWT Bearer com escopos `ledger:read` e `ledger:write`. Em **Development** existe `POST /dev/token` para emitir um token local:

```bash
curl -s -X POST localhost:8080/dev/token -H 'Content-Type: application/json' \
  -d '{"clientId":"avaliador","scopes":["ledger:read","ledger:write"]}'
```

No Windows PowerShell (onde `curl` é alias de `Invoke-WebRequest`):

```powershell
Invoke-RestMethod -Method Post http://localhost:8080/dev/token -ContentType 'application/json' `
  -Body '{"clientId":"avaliador","scopes":["ledger:read","ledger:write"]}'
```

Fora de Development/Testing, a aplicação **não sobe** sem `Auth:Authority` (provedor OIDC), e a chave de desenvolvimento é recusada.

### Endpoints

| Método | Rota | Escopo | Descrição |
|---|---|---|---|
| `POST` | `/v1/accounts` | write | Abre conta (saldo zero, `overdraftLimit` opcional) |
| `GET` | `/v1/accounts/{id}` | read | Dados e saldo corrente |
| `GET` | `/v1/accounts/{id}/balance?at=` | read | Saldo atual ou **posição no instante `at`** (ISO 8601) |
| `POST` | `/v1/accounts/{id}/movements` | write | Crédito/débito. **Exige `Idempotency-Key`** |
| `GET` | `/v1/accounts/{id}/movements?from&to&cursor&limit` | read | Extrato, mais recente primeiro, paginado por cursor |
| `GET` | `/v1/accounts/{id}/movements/{entryId}` | read | Detalhe de um lançamento |
| `POST` | `/v1/accounts/{id}/movements/{entryId}/reversal` | write | Estorno (lançamento compensatório). **Exige `Idempotency-Key`** |
| `GET` | `/health/live`, `/health/ready` | — | Liveness (processo) e readiness (banco) |

Instantes são sempre devolvidos em UTC com sufixo `Z` (ex.: `2026-10-21T22:02:20.451829Z`), então o `occurredAt` de um lançamento pode ser reenviado como está em `?at=`.

### Contrato de respostas

| Situação | Status | `code` |
|---|---|---|
| Lançamento registrado | `201` + `Location` | — |
| Mesma chave e mesmo payload (replay) | `200` + `Idempotent-Replayed: true` | — |
| Validação | `400` | `invalid_amount`, `idempotency_key_required`, … |
| Conta/lançamento inexistente | `404` | `account_not_found`, `entry_not_found` |
| Lançamento já estornado | `409` | `entry_already_reversed` |
| Regra de negócio | `422` | `insufficient_funds`, `idempotency_key_reused`, `account_inactive` |
| Limite por cliente excedido | `429` + `Retry-After` | `rate_limited` |
| Banco indisponível / contenção | `503` + `Retry-After` | `database_unavailable`, `account_contention` |

Todos os erros seguem **RFC 9457 (Problem Details)**, com `code` estável e `traceId` para correlação com logs e traces.

---

## Decisões principais

| Tema | Decisão | Por quê | ADR |
|---|---|---|---|
| Estrutura | Monólito modular, uma API stateless | Um único contexto com consistência forte; distribuir criaria transações distribuídas onde não se pode tê-las | [0001](docs/adr/0001-monolito-modular.md) |
| Persistência | PostgreSQL + SQL explícito (Dapper) | As garantias (constraints, índices parciais, triggers, lock de linha) são o design, e precisam estar visíveis e revisáveis | [0002](docs/adr/0002-postgresql-e-sql-explicito.md) |
| Modelo | Ledger imutável com `balance_after` | Auditabilidade; correção por estorno; saldo em T com uma leitura indexada | [0003](docs/adr/0003-ledger-imutavel.md) |
| Concorrência | UPDATE condicional atômico | Sem janela de corrida, sem tempestade de retries, lock só na conta afetada | [0004](docs/adr/0004-concorrencia-update-condicional.md) |
| Idempotência | `Idempotency-Key` obrigatória, reservada na mesma transação | Torna seguro qualquer retry (cliente, proxy, retry interno) | [0005](docs/adr/0005-idempotencia.md) |
| Leitura | Saldo atual da conta; saldo em T do ledger | Ambos O(log n), independentes do volume | [0006](docs/adr/0006-consultas-de-saldo.md) |
| Integração | Transactional outbox | Eventos nunca divergem do ledger; broker fora do ar não afeta escrita | [0007](docs/adr/0007-transactional-outbox.md) |
| Segurança | JWT + escopos; ledger sem PII | Menor privilégio; superfície de exposição mínima | [0008](docs/adr/0008-seguranca.md) |
| Sobrecarga | Rate limit por cliente, timeouts, falha rápida | Pico vira 429/503 rápido para alguns, não lentidão para todos | [failure-modes](docs/failure-modes.md) |

---

## Estrutura do repositório

```
src/
  Ledger.Domain/           Regras e tipos do negócio (Money, Movement, erros). Sem dependências.
  Ledger.Application/      Casos de uso e portas (ILedgerWriter/ILedgerReader). Não conhece HTTP nem SQL.
  Ledger.Infrastructure/   PostgreSQL: migrations SQL, writer/reader, retry transitório, outbox.
  Ledger.Api/              Endpoints, auth, rate limiting, Problem Details, health checks, OpenTelemetry.
tests/
  Ledger.UnitTests/
  Ledger.IntegrationTests/
docs/
  architecture.md
  failure-modes.md
  adr/
requests.http              Roteiro executável da API
docker-compose.yml         Postgres + API
```

O schema está em [`V001__initial_schema.sql`](src/Ledger.Infrastructure/Persistence/Migrations/V001__initial_schema.sql) e o caminho crítico de escrita em [`PostgresLedgerWriter.cs`](src/Ledger.Infrastructure/Persistence/PostgresLedgerWriter.cs). São os dois arquivos que eu leria primeiro.

---

## O que eu faria com mais tempo

Priorizei a correção do núcleo (escrita concorrente, idempotência, posição histórica) e a documentação das decisões. Itens conscientemente deixados de fora, em ordem de prioridade:

1. **Teste de carga contínuo** (k6/NBomber) com metas explícitas de p95/p99 e throughput por conta quente, rodando no CI noturno.
2. **Lançamentos retroativos** (modelo bitemporal: data de efetivação × data de registro). Hoje a posição é por horário de registro; ver [ADR 0003](docs/adr/0003-ledger-imutavel.md).
3. **Partida dobrada** (contrapartida contábil para cada lançamento), necessária para conciliação com a contabilidade.
4. **Contas muito quentes:** sub-saldos (*sharded balances*) para contas com milhares de lançamentos por segundo; hoje a escrita é serializada por conta.
5. **Réplica de leitura** com roteamento das consultas (`ILedgerReader` já é a fronteira para isso) e **PgBouncer** em modo transação.
6. **Particionamento** de `ledger_entries` por período e política de retenção/arquivamento.
7. **Retenção das chaves de idempotência** (ex.: expurgo após 30 dias), hoje mantidas indefinidamente.
8. **Broker real** no outbox (Azure Service Bus/Kafka) e **teste de contrato** dos eventos.
9. **Role de banco com menor privilégio** (sem UPDATE/DELETE em `ledger_entries`) e migrations executadas por uma role separada no pipeline.
10. **Bloqueio/desbloqueio de contas** via API (o status já existe no modelo e é respeitado na escrita).
11. **Frontend de backoffice** (o protótipo foi desenhado, mas fica fora do escopo obrigatório).
