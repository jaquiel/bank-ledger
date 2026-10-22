# ADR 0006 — Consultas de saldo e modelo de consistência de leitura

**Status:** aceita

## Contexto
O banco precisa da posição consolidada "em um determinado momento". As consultas devem ser rápidas independentemente do tamanho do histórico e ter semântica clara.

## Decisão
- **Saldo atual** (`GET /balance` sem `at`): lido de `accounts.balance`, materializado na mesma transação de cada lançamento. Fortemente consistente.
- **Saldo em T** (`GET /balance?at=T`): `balance_after` do último lançamento com `occurred_at <= T`; zero se não houver lançamento até T. Uma busca indexada.
- O saldo atual **não** é calculado como "saldo em agora", para não depender do relógio da aplicação estar sincronizado com o do banco.
- Consultas com `at` no futuro (além de 1 minuto de tolerância para relógios de clientes) são recusadas com 400.
- A resposta inclui `lastSequence`: o último lançamento considerado, útil para conciliação.
- Instantes são sempre serializados em UTC com sufixo `Z`, de modo que o valor devolvido pela API pode ser reenviado como está em `?at=` (um `+00:00` viraria espaço na query string).
- Extrato com **paginação por cursor** (keyset sobre `sequence`), nunca por offset.

## Semântica para T muito recente
`occurred_at` é atribuído antes do `COMMIT`. Uma consulta com T nos últimos milissegundos pode ainda não ver um lançamento em processo de commit com `occurred_at <= T`. A janela é limitada pela duração da transação (teto: `statement_timeout`). Para T fora dessa janela, a resposta é estável e definitiva, o que o teste `Balance_at_an_instant_is_stable_after_later_movements` verifica.

## Evolução: réplica de leitura
Sob alta demanda de leitura, `ILedgerReader` pode apontar para uma réplica, aceitando defasagem de segundos (o protótipo de backoffice mostra um aviso de "saldo pode estar defasado"). A escrita, e qualquer leitura usada para **decidir** uma escrita, continua no primário. Não implementei a réplica porque não há réplica no ambiente local e a decisão de roteamento depende dos SLOs do banco.

## Consequências
- (+) Custo constante por consulta, independente do histórico.
- (+) Semântica explícita e testada.
- (−) A posição em T reflete o horário de registro, não uma data de efetivação (ver [ADR 0003](0003-ledger-imutavel.md)).
