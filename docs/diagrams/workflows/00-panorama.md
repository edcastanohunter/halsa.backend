# 00 · Panorama: el ciclo de vida de un caso ATEL

Este documento cubre:

- quién interviene en un caso;
- en qué orden;
- qué hace el sistema solo.

Los detalles de cada usuario están en su propio archivo.

## 1. Mapa de actores

```mermaid
flowchart LR
    subgraph ARL["ARL (el tenant)"]
        CORE["Core de la ARL<br/>(sistema)"]:::sistema
        AN["Analista"]:::accion
        ML["Médico laboral"]:::clinico
        CAL["Médico calificador"]:::clinico
        PRIV["Oficial de privacidad"]:::accion
    end

    subgraph IPS["IPS (prestador, atiende a varias ARL)"]
        SIPS["Sistema de la IPS"]:::sistema
        COORD["Coordinador IPS"]:::accion
        MIPS["Médico IPS"]:::clinico
    end

    TRAB["Trabajador<br/>(paciente)"]:::accion
    HALSA(("Halsa")):::pantalla

    CORE -- "FURAT / FUREP<br/>(radica el caso)" --> HALSA
    HALSA -- "webhooks: validado, plazos,<br/>dictamen, emergencia…" --> CORE
    AN -- "asigna, remite,<br/>emite código" --> HALSA
    ML -- "historia clínica,<br/>incapacidades" --> HALSA
    CAL -- "dictamen PCL<br/>y notificación" --> HALSA
    PRIV -- "audita, revisa,<br/>tramita copias de HC" --> HALSA
    COORD -- "asigna médicos<br/>de su IPS" --> HALSA
    MIPS -- "conceptos,<br/>incapacidades" --> HALSA
    SIPS -- "conceptos e incapacidades<br/>(máquina a máquina)" --> HALSA
    TRAB -- "portal: consulta y<br/>solicita copia de HC" --> HALSA

    classDef pantalla fill:#26a69a2e,stroke:#26a69a,stroke-width:1.5px
    classDef accion fill:#26a69a0a,stroke:#26a69a,stroke-width:2px
    classDef clinico fill:#7e57c233,stroke:#9575cd,stroke-width:1.5px
    classDef sistema fill:#78909c2e,stroke:#90a4ae,stroke-width:1.5px,stroke-dasharray:4 3
```

## 2. El caso de punta a punta

```mermaid
sequenceDiagram
    autonumber
    participant CORE as Core ARL
    participant W as Worker (Halsa)
    participant AN as Analista ARL
    participant CO as Coordinador IPS
    participant MI as Médico IPS
    participant CAL as Calificador ARL
    participant PAC as Trabajador
    participant PR as Oficial privacidad

    CORE->>W: FURAT (accidente) o FUREP (enfermedad)
    Note over W: Caso RADICADO.<br/>Se abren los plazos de reporte e investigación ⏱
    W->>W: Valida contra la afiliación y busca duplicados
    alt No afiliado o duplicado
        W-->>CORE: webhook caso.rechazado
        Note over W: Caso RECHAZADO.<br/>Sus plazos abiertos quedan SUSPENDIDOS
    else Válido
        W-->>CORE: webhook caso.validado
        Note over W: Caso VALIDADO
    end

    AN->>AN: Asigna al médico laboral o calificador (por su id)
    AN->>CO: Remite el caso a la IPS (código REPS)
    CO->>MI: Asigna al médico de su IPS
    MI->>MI: Registra conceptos y carga incapacidades 🔒
    Note over W: Cada incapacidad recalcula TOPE_IT_180 ⏱<br/>(alertas en 150, 180 y 540 días)

    AN->>PAC: Emite el código de activación (se entrega fuera del sistema)
    PAC->>PAC: Crea su cuenta, canjea el código y ve su línea de tiempo

    CAL->>CAL: Registra el dictamen PCL 🔒 (el caso pasa a EN CALIFICACIÓN PCL)
    CAL->>PAC: Notifica el dictamen (correo certificado, electrónico o personal)
    Note over W: Se abre CONTROVERSIA_DICTAMEN ⏱ (10 días hábiles)
    W->>W: Vence sin controversia → el dictamen queda EN FIRME

    PAC->>PR: Solicita copia de su historia clínica
    Note over W: Se abre SOLICITUD_COPIA_HC ⏱ (10 días hábiles)
    PR->>PAC: La entrega por el canal formal y cierra la solicitud
```

## 3. Estados del caso

```mermaid
stateDiagram-v2
    [*] --> Radicado: FURAT / FUREP del core de la ARL
    Radicado --> Validado: Worker. Afiliado y no duplicado
    Radicado --> Rechazado: Worker. NO_AFILIADO o DUPLICADO
    Validado --> EnCalificacionPcl: el calificador registra el dictamen
    Validado --> EnAtencion: (sin transición implementada)
    EnAtencion --> EnCalificacionOrigen: (sin transición implementada)
    EnCalificacionOrigen --> EnCalificacionPcl: (sin transición implementada)
    EnCalificacionPcl --> Cerrado: (sin transición implementada)
    Rechazado --> [*]
    Cerrado --> [*]

    classDef pendiente fill:#9e9e9e1a,stroke:#9e9e9e,stroke-width:1.5px,stroke-dasharray:5 5
    class EnAtencion,EnCalificacionOrigen,Cerrado pendiente
```

Reglas que el diagrama no muestra:

- Un caso **rechazado o cerrado** no admite registros clínicos ni dictamen.
- Los códigos de activación solo se emiten desde `Validado` en adelante, nunca para un caso rechazado.
- La remisión a una IPS y la asignación de profesionales no cambian el estado del caso.
- Corregir el dictamen deja el caso en `EnCalificacionPcl` y actualiza el porcentaje.

## 4. Plazos legales (lo que corre solo)

```mermaid
flowchart TD
    E1["Caso radicado"]:::sistema --> R1["REPORTE_FURAT / REPORTE_FUREP<br/>2 días hábiles desde el evento"]:::plazo
    E1 --> R2["INVESTIGACION_AT<br/>15 días calendario (solo accidentes)"]:::plazo
    E1 --> R3["REMISION_INVESTIGACION_GRAVE<br/>15 días (solo AT grave o mortal)"]:::plazo
    E2["Incapacidad registrada"]:::sistema --> R4["TOPE_IT_180<br/>se recalcula con cada prórroga, alerta en el día 150"]:::plazo
    E3["Dictamen notificado"]:::sistema --> R5["CONTROVERSIA_DICTAMEN<br/>10 días hábiles"]:::plazo
    E4["Solicitud de copia de HC"]:::sistema --> R6["SOLICITUD_COPIA_HC<br/>10 días hábiles"]:::plazo

    R1 --> X1{"¿Radicado a tiempo?"}
    X1 -- sí --> C1["CUMPLIDO al nacer"]:::accion
    X1 -- no --> V1["VENCIDO y extemporáneo"]:::riesgo

    R5 --> X5{"¿Vence sin controversia?"}
    X5 -- sí --> F5["El dictamen queda EN FIRME"]:::sistema

    R6 --> X6["Se cumple al cerrar la solicitud"]:::accion

    classDef accion fill:#26a69a0a,stroke:#26a69a,stroke-width:2px
    classDef sistema fill:#78909c2e,stroke:#90a4ae,stroke-width:1.5px,stroke-dasharray:4 3
    classDef plazo fill:#ffb30033,stroke:#ffb300,stroke-width:1.5px
    classDef riesgo fill:#ef535033,stroke:#ef5350,stroke-width:1.5px
```

```mermaid
stateDiagram-v2
    [*] --> ABIERTO: lo abre un evento
    ABIERTO --> CUMPLIDO: un evento lo cumple, o una persona con nota (plazos:write)
    ABIERTO --> VENCIDO: Worker (barrido cada minuto)
    VENCIDO --> CUMPLIDO: cumplimiento tardío (queda extemporáneo)
    ABIERTO --> SUSPENDIDO: el caso se rechazó
    CUMPLIDO --> [*]
    SUSPENDIDO --> [*]
```

- El Worker envía el webhook `plazo.proximo-a-vencer` al llegar `alertaEn`, y `plazo.vencido` al vencer.
- Los días hábiles usan el calendario de festivos de Colombia.
- Las cifras de los plazos están **pendientes de validación jurídica**.
