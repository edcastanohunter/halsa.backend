# 05 · Coordinador de la IPS (`coordinador-ips`)

**Quién es:** quien reparte, dentro de una IPS (clínica o prestador), los casos que le remiten las ARL.

**Permisos:** `casos:read` (solo los casos **remitidos a su IPS**) y `casos:asignar`.

**Contexto:** una IPS no es un tenant. Puede atender casos de varias ARL a la vez y solo ve un caso mientras su remisión esté vigente.

**Usuario de desarrollo:** `coordinador.ips` (IPS `110011234501`).

## Mapa de navegación

```mermaid
flowchart LR
    INI["Inicio<br/>casos recientes"]:::pantalla --> CAS["Casos<br/>(remitidos a su IPS)"]:::pantalla --> RES["Resumen"]:::pantalla
    RES --- EQU["Equipo<br/>asignar y desasignar médicos de su IPS"]:::pantalla
    INI --> CTA["Cuenta"]:::pantalla

    classDef pantalla fill:#26a69a2e,stroke:#26a69a,stroke-width:1.5px
```

## Flujo de trabajo

```mermaid
flowchart TD
    C0["El analista de la ARL remite el caso a la IPS"]:::sistema --> C1["Webhook caso.remitido al sistema de la IPS<br/>+ el caso aparece en Casos"]:::sistema
    C1 --> C2["Abre el caso: datos administrativos<br/>(radicado, tipo, fecha, documento)"]:::pantalla
    C2 --> C3["Elige al médico de su IPS<br/>(fuera del sistema: turnos, especialidad)"]:::externo
    C3 --> C4["Equipo → asignar por identificador<br/>(el sub que el médico ve en su Cuenta)"]:::accion
    C4 --> C5["El médico IPS ya puede abrir<br/>la historia clínica del caso 🔒"]:::clinico
    C2 --> C6["Desasignar: solo las asignaciones<br/>que hizo su propia IPS"]:::accion
    C0 -. "si la ARL revoca la remisión" .-> C7["El caso desaparece de su lista<br/>y sus médicos pierden el acceso"]:::riesgo

    classDef pantalla fill:#26a69a2e,stroke:#26a69a,stroke-width:1.5px
    classDef accion fill:#26a69a0a,stroke:#26a69a,stroke-width:2px
    classDef clinico fill:#7e57c233,stroke:#9575cd,stroke-width:1.5px
    classDef sistema fill:#78909c2e,stroke:#90a4ae,stroke-width:1.5px,stroke-dasharray:4 3
    classDef riesgo fill:#ef535033,stroke:#ef5350,stroke-width:1.5px
    classDef externo fill:#9e9e9e1a,stroke:#9e9e9e,stroke-width:1.5px,stroke-dasharray:2 2
```

El coordinador **no ve nada clínico**, no ve los plazos y no carga incapacidades.
