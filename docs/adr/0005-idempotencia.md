# ADR 0005 — Idempotência obrigatória, reservada na mesma transação

**Status:** aceita

## Contexto
Em redes reais, requisições são repetidas: timeout do cliente, retry de proxy, usuário clicando duas vezes, conexão que cai durante o `COMMIT` (o cliente não sabe se gravou). Sem idempotência, cada retry pode virar um lançamento duplicado, ou seja, dinheiro debitado duas vezes.

## Decisão
- Toda escrita (lançamento e estorno) **exige** o header `Idempotency-Key`, no padrão do draft IETF `httpapi-idempotency-key-header`.
- Escopo da chave: **(cliente autenticado, chave)**. Clientes diferentes podem usar a mesma chave sem colisão.
- A chave é **reservada na mesma transação** do lançamento, com `INSERT ... ON CONFLICT DO NOTHING`, junto a um SHA-256 do payload normalizado.
- Se a chave já existe:
  - mesmo payload: devolve o lançamento original (`200` + `Idempotent-Replayed: true`);
  - payload diferente: `422 idempotency_key_reused`.
- Requisições **concorrentes** com a mesma chave: a segunda espera no índice único até a primeira terminar e então faz replay. Não há janela entre "verificar" e "gravar".
- Rejeições de negócio (saldo insuficiente, validação) **não** consomem a chave: a transação inteira sofre rollback e nada foi movimentado.

## Alternativas consideradas
- **Idempotência opcional:** o cliente que não envia a chave fica exposto a duplicidade. Em sistema financeiro, deve ser obrigatória.
- **Cache (Redis) de chaves:** não é atômico com o lançamento; uma falha entre gravar o lançamento e gravar a chave abre a duplicidade que queríamos evitar.
- **Armazenar o resultado das rejeições também** (como fazem alguns provedores de pagamento): torna o retry de uma rejeição sempre igual. Optei por não armazenar porque nada foi movimentado e reavaliar é o comportamento mais útil (ex.: débito recusado pode passar depois de um crédito). O teste `A_rejected_operation_does_not_consume_its_key` documenta essa semântica.

## Consequências
- (+) Qualquer retry, em qualquer camada, é seguro. Isso também viabiliza o retry interno automático em falhas transitórias do banco.
- (+) Resultado incerto no `COMMIT` deixa de ser problema: o cliente repete e recebe o replay ou o lançamento.
- (−) Tabela de chaves cresce indefinidamente. Evolução: expurgo após uma janela de retenção (ex.: 30 dias), comunicada no contrato da API.
