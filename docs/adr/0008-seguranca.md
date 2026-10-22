# ADR 0008 — Segurança e proteção de dados

**Status:** aceita

## Contexto
O sistema manipula dados financeiros sensíveis. A melhor proteção de um dado é não precisar dele.

## Decisão
**Minimização de dados**
- O ledger **não armazena PII**. Conhece apenas identificadores de conta (UUID); nome, CPF, endereço pertencem ao contexto de cadastro.
- Eventos publicados não incluem a descrição livre. Logs não registram payloads de requisição.
- Identificadores UUID v7: não enumeráveis e ainda ordenáveis no índice.

**Autenticação e autorização**
- JWT Bearer validado contra o provedor OIDC do banco (`Auth:Authority`), com validação de emissor, audiência, assinatura e expiração.
- Escopos separados `ledger:read` e `ledger:write` (menor privilégio). Escrita exige identificar o cliente, que compõe o escopo da idempotência e a trilha de auditoria (`created_by`, `correlation_id` em cada lançamento).
- **Configuração insegura impede a subida:** fora de Development/Testing, a aplicação não inicia sem `Auth:Authority`, e a chave simétrica de desenvolvimento é recusada. `POST /dev/token`, OpenAPI e Scalar só existem em Development.

**Defesa em profundidade**
- Invariantes no banco (imutabilidade, limite de saldo), independentes do código.
- Erros no formato Problem Details com `code` e `traceId`; stack traces e mensagens internas só no log.
- Rate limiting por cliente e timeouts limitam abuso e esgotamento de recursos.
- Container roda como usuário sem privilégios.

## Fora do escopo do código (responsabilidade da plataforma)
- TLS terminado no ingress/API gateway; HSTS habilitado fora de Development.
- Criptografia em repouso do banco e dos backups (padrão em PostgreSQL gerenciado).
- Segredos em cofre (Azure Key Vault ou equivalente), com rotação; nada sensível em `appsettings` de produção.
- Rede privada entre API e banco; banco sem endpoint público.
- Role de banco da aplicação sem `UPDATE`/`DELETE`/`TRUNCATE` em `ledger_entries`.

## Consequências
- (+) Vazamento do banco do ledger expõe valores e identificadores opacos, não identidades.
- (−) Consultas que precisam de dados cadastrais exigem composição com outro serviço (o que é desejável do ponto de vista de fronteira de contexto).
