# Modos de falha

Como o sistema se comporta em cada cenário de falha ou estresse, e por quê. O princípio geral: **em dúvida, não movimentar**, e sempre devolver ao cliente uma resposta que permita repetir a operação com segurança.

| # | Cenário | Comportamento | Resposta ao cliente | Por quê |
|---|---|---|---|---|
| 1 | Cliente reenvia a requisição (timeout de rede, retry automático) | A chave já existe: nenhum novo lançamento; devolve o original | `200` + `Idempotent-Replayed: true` | Retry precisa ser seguro por construção, não por cuidado do cliente |
| 2 | Duas requisições com a mesma chave ao mesmo tempo | A segunda espera no índice único da chave e faz replay | `201` para uma, `200` para a outra | O banco serializa; nenhuma lógica de "verificar antes" com janela de corrida |
| 3 | Mesma chave, payload diferente | Rejeita; nada é movimentado | `422 idempotency_key_reused` | Indica bug no cliente que esconderia um lançamento |
| 4 | Débitos concorrentes na mesma conta | Serializados pelo lock da linha; cada um avalia o saldo já atualizado pelo anterior | `201` ou `422 insufficient_funds` | UPDATE condicional atômico ([ADR 0004](adr/0004-concorrencia-update-condicional.md)) |
| 5 | Conta muito disputada (fila de locks) | Espera até `lock_timeout` (3 s); depois desiste sem movimentar | `503 account_contention` + `Retry-After` | Fila infinita consome conexões e derruba o sistema inteiro |
| 6 | Banco lento | Comando abortado em `statement_timeout` (5 s); requisição HTTP em 10 s | `503 database_timeout` / `504` | Limites explícitos evitam esgotamento do pool |
| 7 | Queda de conexão, deadlock, failover | Retry interno curto (até 2 tentativas, backoff com jitter) da transação inteira | Transparente, ou `503` se persistir | Seguro porque a transação é idempotente |
| 8 | Banco indisponível | Escritas falham rápido, readiness fica *unhealthy* (balanceador para de enviar tráfego) | `503 database_unavailable` + `Retry-After` | Não aceitamos escrita que não podemos confirmar (ver abaixo) |
| 9 | Conexão cai **durante o COMMIT** (resultado incerto) | Cliente recebe 503 sem saber se gravou | `503` | Ao repetir com a mesma chave: se gravou, recebe replay; se não, grava agora. Nunca duplica |
| 10 | Broker de eventos indisponível | Eventos acumulam no outbox; escrita **não** é afetada; publicação retomada depois | Nenhum impacto | Outbox desacopla o ledger da integração ([ADR 0007](adr/0007-transactional-outbox.md)) |
| 11 | Instância da API morre no meio de uma requisição | Transação não commitada sofre rollback pelo banco | Cliente recebe erro de conexão e repete com a mesma chave | Atomicidade + idempotência |
| 12 | Pico de tráfego / cliente ruidoso | Token bucket por cliente rejeita o excedente sem fila | `429 rate_limited` + `Retry-After` | Rejeitar cedo protege os demais clientes e o banco |
| 13 | Bug na aplicação tenta alterar ou apagar lançamento | Trigger rejeita | `500` (e alerta) | Imutabilidade garantida no banco; erro não transitório não sugere retry |
| 14 | Bug na aplicação tenta deixar saldo abaixo do limite | `CHECK` rejeita | `500` (e alerta) | Defesa em profundidade |
| 15 | Configuração de produção sem provedor de identidade | A aplicação não sobe | — | Falhar na subida é melhor que aceitar tokens de desenvolvimento |

Os cenários 1–4, 8, 12, 13 e 14 são cobertos por testes de integração automatizados.

## Decisão deliberada: não enfileirar escritas quando o banco cai

Uma alternativa comum é aceitar a requisição (`202 Accepted`), colocá-la numa fila e processar quando o banco voltar. Descartei porque:

- **Débito tem resposta de negócio.** "Saldo insuficiente" só é conhecido ao aplicar a regra sobre o saldo real. Com `202`, o cliente acharia que debitou e descobriria a recusa depois, em outro canal.
- **A fila vira um segundo lugar com dinheiro em trânsito**, que precisa de conciliação, monitoramento e recuperação próprios.
- **O ganho é aparente:** a indisponibilidade do banco continua impedindo a confirmação; só muda quem descobre isso e quando.

Com idempotência, a falha rápida já dá ao cliente o que importa: uma forma segura de tentar de novo.

## Escopo de disponibilidade

A disponibilidade de escrita é a do PostgreSQL primário. Em produção, isso significa um serviço gerenciado com alta disponibilidade (ex.: Azure Database for PostgreSQL com zona redundante), cujo failover leva segundos e é absorvido pelos itens 7 a 9 acima. Leituras podem continuar em réplica durante a indisponibilidade do primário, aceitando defasagem (ver [ADR 0006](adr/0006-consultas-de-saldo.md)).
