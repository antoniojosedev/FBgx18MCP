# Complemento da #353 — Save headless não propagou descrição nesta GX18.0.15

Obrigado pelo experimento positivo na GX18.0.10.184260 e pela distinção entre salvar a instância e usar os overloads de reapply. Repetimos a alteração de `selection/@description` em uma KB sem usuários, com baseline frozen, e obtivemos um resultado diferente. Não consideramos isso uma refutação do seu experimento: queremos identificar qual pré-condição explica a diferença.

## Ambiente e limite da prova

- MCP carregado nesta sessão: 3.11.1; SDK efetivo: 18.0.15.188745, driver native-sdk, worker isolado.
- WorkWith padrão GeneXus 1.2, aplicado a uma Transaction existente. O XML da instância tem `updateTransaction="Apply WW Style"`.
- Na IDE, a opção **Apply this pattern on save** já estava marcada antes do teste e não foi alterada.
- Os nomes e descrições abaixo são aliases sanitizados. O teste usou objetos existentes, não criou `TestTrn`.
- Não executamos build, aplicação web, XPZ import/export ou chamada interna `GenerateInstanceObjects`. Medimos instância persistida e conteúdo do Events do painel gerado; não auditamos toda a família derivada.

## Protocolo e resultado

1. Ler a instância e `WW<TestTrn>.Events`, conservando texto e token de versão.
2. Executar `genexus_edit` em patch com `part="PatternInstance"`, `expectedCount=1`, `baseVersion`, `rollbackOnFailure=true` e `verifyMode="exact"`. O único delta é uma descrição existente de Selection. Primeiro dryRun; depois o mesmo patch persistido.
3. Fechar e reabrir a KB pelo MCP, então reler instância e painel por GUID. O caption anterior persistiu nas duas rotas; `EVIDENCIAS.md` registra tokens e hashes capturados. Esse passo mitigou a leitura antiga observada com a IDE, descrita em relato separado, mas não instrumentamos a invalidação de cada cache nem cruzamos `inspect` após cada patch. Não afirmamos ter excluído todas as camadas de cache.
4. Restaurar pelo mesmo caminho e repetir a releitura. Ambos os conteúdos retornaram exatamente ao baseline.

| Rota | Instância após Save e reabertura | Events do painel gerado |
|---|---|---|
| Nome qualificado do WorkWith | Descrição temporária persistida | Texto byte a byte igual ao baseline; caption e token anteriores |
| Nome qualificado da Transaction, `type="Transaction"` | Descrição temporária persistida na mesma instância resolvida | Texto byte a byte igual ao baseline; caption e token anteriores |
| IDE, mesma propriedade | Descrição temporária persistida | `Form.Caption` contém a descrição temporária; log de geração do pattern termina com sucesso |

A segunda rota foi testada especificamente porque seu exemplo usa `genexus_edit name=Trn353 part=PatternInstance`. Aqui incluímos `name="Root Module/Pasta/TestTrn"`, `type="Transaction"` e `part="PatternInstance"` (nomes sanitizados). Ela resolveu o mesmo GUID da instância que a primeira. Em ambas as escritas headless recebemos `WriteApplied`, `sdkSaveCompleted=true` e persistência verificada; a resposta não trouxe `derivedObjectRegeneration`. O campo depende de reconhecimento do manifest na escrita; sua ausência não prova qual ramo foi executado. O anexo registra o recibo e o schema usado.

No controle pela IDE, o usuário salvou a descrição com a opção de aplicação automática já marcada, conforme sua confirmação e screenshot. O log integral fornecido pelo usuário registrou Save bem-sucedido dos painéis, componentes, procedures, Transaction e lista de programas gerados; o anexo conserva a enumeração sanitizada. A nova descrição apareceu no Events do painel lido por GUID. Depois o usuário restaurou a descrição e salvou; fechar/reabrir a KB pelo MCP confirmou o texto original. O log e o caption sustentam o controle positivo observado; não estamos equiparando alteração de token a regeneração.

## Como podemos ajudar a isolar

Este é um relato de execução, ainda sem uma reprodução mínima portável: não publicamos a estrutura integral da KB. O anexo distingue capturas preservadas, observações registradas na sessão e templates sanitizados de requests. Os templates não são transcrições completas das chamadas originais e exigem obter a versão atual da fixture usada pelo mantenedor. A ligação de identidade entre Transaction e instância foi observada na resolução, mas os GUIDs originais não estão no anexo e essa igualdade não pode ser auditada independentemente por ele.

Antes de mudar o bloqueio de reapply, seria útil comparar GX18.0.10 e GX18.0.15, as preferências usadas no Save e os callbacks da instância. `WriteService.PatternWrite.cs` contém um caminho com `ForceSave=true`, `ForceSaveDefaultParts=true` e `SkipValidation=true`, além de uma alternativa experimental anterior a ele; o trecho está no anexo. Não instrumentamos qual caminho foi executado. Seu experimento levantou os hooks como hipótese. São pistas, não causa estabelecida. Também pode haver diferença de estado/configuração do pattern entre as KBs.

Um próximo ensaio do mantenedor pode registrar: SDK efetivo, pattern resolvido, estado de aplicação automática antes/depois do Save, preferências de Save e conteúdo de um controle gerado cujo valor depende da descrição. Ter o mesmo discriminador em WebForm e Events nas duas instalações tornaria a comparação mais forte.

Aceite proposto: documentar as pré-condições em que o Save headless propaga a propriedade, ou identificar/corrigir o caminho que persiste a instância sem atualizar o derivado. Não pedimos afirmar regeneração universal nem remover o gate conservador de reapply. Este complemento não envolve autoria estrutural de filtros.
