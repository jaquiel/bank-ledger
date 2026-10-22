Desafio Técnico

## Contexto

Você aceitou um contrato de consultoria para um banco digital que está enfrentando problemas de performance e estabilidade em um de seus sistemas internos, responsável por registrar as movimentações financeiras de
clientes (entradas e saídas de valores em suas contas) e por fornecer, sob demanda, a posição consolidada de saldo de cada cliente ao longo do tempo.

O sistema atual é antigo, foi crescendo sem muito planejamento e hoje apresenta lentidão, instabilidade em horários de pico e dificuldade de manutenção. O banco pediu que você repense essa solução do zero, aplicando seu
conhecimento técnico para entregar algo robusto, apto a operar em um ambiente financeiro real.

## Problema de negócio

O banco precisa de uma solução que:

    Registre as movimentações financeiras dos clientes (créditos e débitos) em suas contas;
    Permita consultar a posição consolidada (saldo) de um cliente em um determinado momento;
    Continue operando de forma confiável mesmo em cenários de instabilidade, alta demanda ou falhas parciais, considerando que se trata de dados financeiros sensíveis e que qualquer inconsistência gera impacto direto ao
    cliente e à operação do banco.

## O que esperamos de você

Você deve pensar e justificar sua solução como um profissional que atuará tomando decisões técnicas e arquiteturais no dia a dia do banco. Não existe uma única resposta certa — queremos entender como você analisa o
problema, quais trade-offs você considera e por quê.

Fique à vontade para definir:

    Como estruturar a solução (uma ou mais aplicações, camadas, módulos, etc.);
    Quais tecnologias, frameworks e padrões utilizar e por quê;
    Como tratar consistência de dados, concorrência e cenários de falha;
    Como a solução se comportaria sob alta demanda ou indisponibilidade parcial;
    Como proteger dados sensíveis dos clientes;
    O que você consideraria essencial documentar para que outra pessoa do time entenda e evolua sua solução.

## Requisitos obrigatórios

    Implementação em C#;
    Testes automatizados;
    Código hospedado em repositório público no GitHub;
    README com instruções claras de como rodar a aplicação localmente;
    Toda documentação do projeto deve estar no próprio repositório.
    Código fonte deve compilar sem erros e warnings

Caso os requisitos obrigatórios acima não sejam minimamente atendidos, o teste será desconsiderado.

## Observações

    Você não precisa esgotar todas as possibilidades técnicas do problema — use seu tempo para demonstrar como você pensa e prioriza, não para implementar tudo.
    Estamos interessados em entender suas decisões tanto quanto no código entregue. Aproveite a documentação do projeto para explicar escolhas, alternativas consideradas e o que você faria diferente com mais tempo.

Boa sorte!
