# ADR 0001 — Monólito modular em um único serviço

**Status:** aceita

## Contexto
O sistema atual cresceu sem planejamento e sofre com lentidão e instabilidade. A tentação ao "repensar do zero" é distribuir: serviço de escrita, serviço de saldo, mensageria entre eles. Mas registrar uma movimentação e atualizar o saldo precisam ser **uma unidade atômica**: um débito só é válido se o saldo do instante em que é aplicado o comporta.

## Decisão
Um único serviço HTTP **stateless**, organizado em módulos com dependências em uma direção (`Domain ← Application ← Infrastructure ← Api`), sobre um único banco PostgreSQL.

## Alternativas consideradas
- **Microsserviços escrita/leitura com mensageria (CQRS distribuído):** o saldo lido passaria a ser eventualmente consistente, e a regra "não debitar além do saldo" exigiria saga ou lock distribuído. Mais peças, mais modos de falha, sem ganho real para o volume esperado.
- **Event sourcing com framework dedicado:** o ledger imutável já é, na prática, um log de eventos, e `accounts` é o snapshot. Obtenho o benefício (histórico completo, reconstrução) sem a complexidade operacional de um event store.

## Consequências
- (+) Consistência forte com transações locais; poucas peças para operar e entender.
- (+) Escala horizontal da API é trivial (stateless).
- (−) O banco é o ponto de coordenação. Mitigações: leituras indexadas O(log n), réplica de leitura, particionamento (README, "com mais tempo").
- As fronteiras de módulo (portas `ILedgerWriter`/`ILedgerReader`) permitem extrair partes no futuro se houver motivo medido, não especulativo.
