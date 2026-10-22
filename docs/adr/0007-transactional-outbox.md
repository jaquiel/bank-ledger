# ADR 0007 — Transactional outbox para eventos de integração

**Status:** aceita

## Contexto
Outros sistemas do banco (notificações, antifraude, contabilidade) precisam saber que um lançamento ocorreu. Publicar no broker depois do `COMMIT` cria um *dual-write*: se o processo cai entre os dois passos, o lançamento existe e o evento nunca sai (ou o contrário, se publicar antes).

## Decisão
- O evento `ledger.entry_recorded.v1` é gravado em `outbox_messages` **na mesma transação** do lançamento.
- Um `BackgroundService` lê mensagens pendentes com `FOR UPDATE SKIP LOCKED` (várias instâncias publicam em paralelo sem duplicar trabalho), publica e marca `published_at`.
- Entrega **pelo menos uma vez**: consumidores deduplicam pelo id da mensagem.
- O payload **não** contém a descrição livre (pode conter dados pessoais digitados pelo operador).
- A publicação é feita por `IIntegrationEventPublisher`. A implementação atual registra o evento no log; trocar por Service Bus/Kafka não toca no ledger.

## Alternativas consideradas
- **Publicar direto no broker dentro da requisição:** acopla a disponibilidade da escrita à do broker e mantém o dual-write.
- **CDC (Debezium) sobre o WAL:** excelente em escala, mas exige infraestrutura adicional desproporcional ao escopo.

## Consequências
- (+) Evento e lançamento nunca divergem.
- (+) Broker fora do ar não afeta o registro de movimentações; eventos acumulam e saem depois.
- (−) Latência de publicação de até um ciclo de polling (1 s, configurável).
- (−) Falta expurgo das mensagens publicadas e alerta por backlog (`attempts`, `last_error` já estão no schema para isso).
