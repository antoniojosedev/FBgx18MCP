# Snapshot canônico no salvamento isolado de Events

Uma edição idempotente de Events com `requireObjectSave=true` podia terminar em
`ObjectSaveIncomplete`: Rules aparecia vazia antes do Save e preenchida depois,
embora a leitura independente já retornasse a mesma fonte nos dois momentos.

No SDK U16, a serialização nativa de uma parte de fonte pode consumir um campo
interno ainda não materializado. O snapshot agora lê `ISource.Source` antes de
serializar. Não chama o setter nem salva Rules. A comparação usa fonte ou XML
canônico; a restauração mantém os bytes nativos quando disponíveis.

A prévia captura e compara duas instâncias SDK pelo mesmo capturador do Save.
O Save exclui somente Events da comparação de partes, usando explicitamente a
sobrecarga de lista. Rules, Variables, WebForm, Pattern, propriedades e o
inventário dos demais objetos continuam protegidos antes do commit e na
releitura posterior. Uma mudança real em parte preservada impede o commit.

O recibo expõe formatos, comprimentos e hashes das partes preservadas antes e
depois, sem incluir suas fontes. `partPersisted` descreve a comparação da parte;
`persisted` exige também commit. `transactionCommitted` e `idempotent` tornam o
resultado inequívoco. `isolationTimings` separa o custo do Save dos três
inventários. Um timeout continua exigindo consultar a operação já enviada;
não se deve repetir o Save.

O caminho já auditado de Save isolado reconhece também o conjunto coerente
U16 `18.0.16.189595`, mantendo o conjunto anterior e recusando mistura de seus
componentes. Isso não modifica a compatibilidade geral por major do Worker.
Os aliases de concorrência `baseVersion` e `expectedVersion` usam a mesma
barreira; valores conflitantes são recusados antes do despacho.

A regressão usa uma parte sintética com fonte materializada sob demanda e
serializador que consome o cache. Reverter a ordem reproduz a fonte vazia e
faz o teste falhar. O teste também distingue uma mudança substantiva em Rules.

Na distribuição operacional com esta correção, a validação nativa U16 incluiu
WebPanel sintético com Rules não vazias, prévia sem escrita, Save real, Save
idempotente, token obsoleto e uma alteração inesperada de Rules injetada apenas
no pacote privado de ensaio, com rollback verificado. Um WebPanel com host
WorkWithPlus também concluiu Save idempotente e releitura independente das
partes preservadas. Essa evidência corresponde aos cenários executados e não
substitui novas validações para SDKs ou callbacks ainda não auditados.

## Bundle U16 com WorkWithPlus 16.2

O bundle `18.0.16.189595+wwp16.2.0.8386` tem um manifesto versionado em
`config/events-u16-wwp16.2-audit.json`, incorporado no Worker e distribuído como
arquivo idêntico. São 452 arquivos do SDK e WWP, incluindo recursos de idioma.
Todas as nove DLLs centrais são obrigatórias. Arquivo ausente, hash diferente,
mistura de builds ou implementação SDK carregada fora do conjunto recusam o
Save isolado. A compatibilidade geral por major permanece independente.

O ensaio de 14/10/2026 usou o Worker nativo e um WebPanel sintético com
instância WWP `Empty` persistida. Prévia sem mutação, Save real, Save
idempotente, conflito antes de escrita, releituras de todas as partes e
metadados dos demais objetos foram comprovados. Uma alteração de Rules foi
injetada somente no pacote privado de ensaio e no nome sintético autorizado:
impediu o commit, provocou rollback atômico e deixou fonte, revisão e token
iguais ao estado anterior. O objeto e sua instância foram removidos ao final.

Os Saves longos atingiram `WorkerTimeout`. Consultou-se a mesma operação por
`genexus_lifecycle(action='status', target='op:...')` e, ao chegar ao estado
terminal, por `action='result'`. O recibo completo fica em `workerPayload` na
consulta de resultado; status não precisa conter esse corpo. Nenhum Save foi
reenviado. O timeout síncrono não altera o resultado terminal do worker.

As APIs de isolamento continuam `EventsSuspended` nos brokers global e da KB
e a flag transitória `SkipApplyPattern`, restauradas ao sair. A inspeção do
SDK confirmou o teste dessa flag em `Package.OnAfterSave` antes de aplicar
Pattern. Os vinte subscribers WWP identificados usam o broker; hooks de
documento da UI não integram o Save nativo. Corpos protegidos do SDK exigiram
as provas nativas: leitura de IL não foi apresentada como prova exclusiva.
Não se altera `SDPlus_Editor_Apply_On_Save` para False, nem se chama Specify,
Generate, Build, Rebuild, reorg, deploy ou execução de aplicação.

Durante a auditoria, a build candidata permitia o bundle novo apenas no
objeto sintético exato. A autorização final consta do manifesto incorporado
somente depois das provas. Alterar o JSON externo não muda a autorização.

## Bundle U11 com WorkWithPlus 16.1

O bundle `18.0.11.54344+wwp16.1.2.8167` segue o mesmo mecanismo, com o manifesto
`config/events-u11-wwp16.1-audit.json` (459 arquivos). Sua prova nativa, de
28/10/2026, vale para uma KB sem nenhuma instância de padrão
(`nativeVerification.scope = kb-without-pattern-instances`): o Worker conta os
`PatternInstance` do modelo inteiro depois de verificar o bundle e recusa o Save
como `PatternInstancesNotVerified` se houver qualquer um ou se a contagem falhar.
O ensaio salvou um WebPanel sintético pelo pipeline protegido (prévia sem mutação,
token obsoleto recusado, Save assíncrono com recibo completo, releitura fresca,
reinício do Worker, Save idempotente, erro de sintaxe com rollback verificado e
fila com uma vencedora e uma perdedora `VersionConflict`) e removeu os objetos
sintéticos ao final. O resultado sanitizado está em
`docs/evidence/events-u11-wwp16.1-native-proof.json`; o SHA-256 desse arquivo é o
`proofEvidenceSha256` do manifesto. Não houve ensaio com host WorkWithPlus
persistido. Detalhes em [Required Events save isolation](events-object-save-isolation.md).
