# ADR 0002 — PostgreSQL com SQL explícito (Dapper) e migrations em SQL

**Status:** aceita

## Contexto
As garantias mais importantes do sistema (lock de linha na conta, UPDATE condicional, índices únicos parciais, triggers de imutabilidade, CHECK de saldo) são recursos do banco. Elas **são** o design, não um detalhe de persistência.

## Decisão
- **PostgreSQL**: transações ACID maduras, MVCC, `ON CONFLICT`, `FOR UPDATE SKIP LOCKED`, índices parciais, `numeric` exato, alta disponibilidade gerenciada em todas as nuvens.
- **Npgsql + Dapper com SQL explícito**, em vez de EF Core. Cada comando do caminho crítico é visível, revisável e testável como está.
- **Migrations em arquivos `.sql` versionados**, embutidos no assembly e aplicados por um executor mínimo com advisory lock (duas instâncias nunca migram ao mesmo tempo).

## Alternativas consideradas
- **EF Core:** produtivo para CRUD, mas o caminho crítico teria de ser SQL cru de qualquer forma (UPDATE condicional com `RETURNING`, `ON CONFLICT DO NOTHING`), e triggers/índices parciais ficariam escondidos em código de migration gerado. Ganharia pouco e perderia transparência.
- **SQLite:** discutido explicitamente. Lock de banco inteiro (não de linha), sem decimal nativo (aritmética em ponto flutuante no SQL) e arquivo local (impede múltiplas instâncias). Os testes de concorrência passariam trivialmente sem provar nada.
- **Banco em memória nos testes:** não tem locks, constraints nem triggers reais.

## Consequências
- (+) Garantias explícitas e verificáveis; os testes de integração rodam contra Postgres real (Testcontainers).
- (−) Mais SQL escrito à mão. Mitigado por ser pouco (cerca de 15 comandos) e totalmente coberto por testes de integração.
- Em produção, migrations devem rodar como passo do pipeline com uma role própria; a aplicação roda com uma role sem `UPDATE`/`DELETE` em `ledger_entries`.
