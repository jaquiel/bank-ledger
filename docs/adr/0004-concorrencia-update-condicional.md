# ADR 0004 — Concorrência por UPDATE condicional atômico

**Status:** aceita

## Contexto
Este é o ponto onde sistemas financeiros mais costumam falhar. O padrão ingênuo "ler saldo → verificar → gravar novo saldo" tem uma janela de corrida: duas transações leem o mesmo saldo, ambas aprovam, e uma atualização se perde (ou a conta fica negativa).

## Decisão
A regra de saldo e a atualização acontecem em **um único statement**, avaliado pelo banco sob o lock da linha:

```sql
UPDATE accounts
   SET balance = balance + @Delta, version = version + 1, updated_at = clock_timestamp()
 WHERE id = @AccountId
   AND status = 'active'
   AND balance + @Delta >= -overdraft_limit
RETURNING balance, version, updated_at;
```

- **1 linha afetada:** aplicado. `version` vira o `sequence` do lançamento e `balance` vira `balance_after`.
- **0 linhas:** recusado. Só então uma leitura extra diagnostica o motivo (conta inexistente, inativa ou saldo insuficiente).
- Isolamento **Read Committed** basta: no PostgreSQL, quando o UPDATE espera pelo lock, ele reavalia o `WHERE` sobre a versão mais recente da linha.
- `lock_timeout` (3 s) limita a espera em contas muito disputadas; ao estourar, a requisição recebe 503 sem movimentar nada.

## Alternativas consideradas
- **Lock otimista (`WHERE version = @lida`) com retry:** correto, mas sob contenção na mesma conta gera tempestade de retries, latência imprevisível e desperdício de conexões. O lock curto de linha dá fila ordenada e previsível.
- **`SELECT ... FOR UPDATE` + UPDATE:** correto, porém dois round-trips com o lock seguro entre eles. O UPDATE condicional faz o mesmo em um.
- **Isolamento Serializable:** correto, mas transfere a resolução de conflitos para retries de `40001`, com o mesmo problema do lock otimista.
- **Lock distribuído (Redis):** mais uma peça, mais um modo de falha, e o banco continuaria precisando garantir a regra.

## Consequências
- (+) Não existe janela de corrida, por construção. O `CHECK (balance >= -overdraft_limit)` no banco é uma segunda linha de defesa.
- (+) Contas diferentes não competem entre si.
- (−) Escritas na **mesma** conta são serializadas. Para contas extremamente quentes (milhares de operações por segundo numa só conta), a evolução é dividir o saldo em sub-saldos.
- **Verificação:** os testes de concorrência foram submetidos a teste de mutação; trocando este UPDATE pelo padrão ler-modificar-escrever, os três testes falham (atualizações perdidas e cadeia `balance_after` inconsistente).
