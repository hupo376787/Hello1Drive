import { dotnet } from './_framework/dotnet.js';

const runtime = await dotnet
    .withDiagnosticTracing(false)
    .withApplicationArgumentsFromQuery()
    .create();

await dotnet.run();
