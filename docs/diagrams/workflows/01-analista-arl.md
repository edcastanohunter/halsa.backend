# 01 · Analista de la ARL (`analista-arl`)

**Quién es:** el gestor administrativo del caso dentro de una ARL.

**Permisos:** `casos:read`, `casos:write`, `casos:asignar`, `incapacidades:read`, `plazos:read`, `plazos:write`, `pacientes:activar`.

**Lo que nunca ve:** diagnósticos, conceptos médicos, historia clínica ni los fundamentos del dictamen. Del dictamen solo ve el **porcentaje de PCL**, que necesita para liquidar.

**Usuarios de desarrollo:** `analista.arla` (ARL_A) y `analista.arlb` (ARL_B).

## Mapa de navegación

```mermaid
flowchart LR
    INI["Inicio<br/>plazos por vencer, casos por estado,<br/>casos recientes"]:::pantalla
    CAS["Casos<br/>filtro por estado"]:::pantalla
    PLA["Plazos<br/>tablero del tenant"]:::pantalla
    CTA["Cuenta"]:::pantalla

    subgraph DET["Detalle del caso"]
        RES["Resumen"]:::pantalla
        EQU["Equipo y remisiones"]:::pantalla
        PCA["Plazos del caso"]:::pantalla
        INC["Incapacidades<br/>(solo lectura)"]:::pantalla
        POR["Portal del paciente<br/>(código de activación)"]:::pantalla
    end

    INI --> CAS --> RES
    INI --> PLA
    RES --- EQU --- PCA --- INC --- POR
    PLA -- "radicado" --> PCA
    INI --> CTA

    NO["Historia clínica 🔒<br/>Dictamen PCL 🔒"]:::riesgo
    RES -. "no aparecen" .- NO

    classDef pantalla fill:#26a69a2e,stroke:#26a69a,stroke-width:1.5px
    classDef riesgo fill:#ef535033,stroke:#ef5350,stroke-width:1.5px
```

## Flujo de trabajo principal

```mermaid
flowchart TD
    A0["Llega un caso nuevo<br/>(FURAT/FUREP del core)"]:::sistema --> A1["Casos: aparece como RADICADO"]:::pantalla
    A1 --> A2{"Validación del Worker"}:::sistema
    A2 -- "rechazado<br/>(no afiliado o duplicado)" --> A3["Estado RECHAZADO.<br/>Los plazos quedan suspendidos.<br/>El core recibe caso.rechazado"]:::riesgo
    A2 -- "validado" --> A4["Estado VALIDADO"]:::pantalla

    A4 --> B1["Equipo: asigna al médico laboral<br/>o al calificador (por su identificador)"]:::accion
    A4 --> B2["Equipo: remite a una IPS<br/>(código REPS de 12 dígitos)"]:::accion
    B2 --> B3["La IPS ve el caso.<br/>Su coordinador asigna al médico IPS"]:::sistema
    A4 --> B4["Portal del paciente:<br/>genera el código de activación"]:::accion
    B4 --> B5["Se muestra una sola vez.<br/>Vence en 30 días"]:::riesgo
    B5 --> B6["Lo entrega al trabajador<br/>(teléfono, carta, en persona)"]:::externo

    A4 --> C1["Plazos: vigila los que están por vencer"]:::plazo
    C1 --> C2{"¿Se cumplió por fuera<br/>del sistema?"}
    C2 -- sí --> C3["Marcar como cumplido<br/>(nota de 10–1000 caracteres)"]:::accion
    C2 -- no --> C4["El Worker lo vence y<br/>avisa al core por webhook"]:::sistema

    A4 --> D1["Incapacidades: revisa días acumulados<br/>y alertas de 150, 180 y 540 días"]:::pantalla
    D1 --> D2{"¿Pasa de 180 días?"}
    D2 -- sí --> D3["Coordina el concepto de rehabilitación<br/>con el médico laboral"]:::externo

    A4 --> E1["Cuando el calificador registra el dictamen:<br/>el caso pasa a EN CALIFICACIÓN PCL<br/>y el analista ve solo el porcentaje"]:::sistema

    classDef pantalla fill:#26a69a2e,stroke:#26a69a,stroke-width:1.5px
    classDef accion fill:#26a69a0a,stroke:#26a69a,stroke-width:2px
    classDef sistema fill:#78909c2e,stroke:#90a4ae,stroke-width:1.5px,stroke-dasharray:4 3
    classDef plazo fill:#ffb30033,stroke:#ffb300,stroke-width:1.5px
    classDef riesgo fill:#ef535033,stroke:#ef5350,stroke-width:1.5px
    classDef externo fill:#9e9e9e1a,stroke:#9e9e9e,stroke-width:1.5px,stroke-dasharray:2 2
```

## El código de activación, paso a paso

```mermaid
sequenceDiagram
    autonumber
    actor AN as Analista
    participant UI as Backoffice
    participant API as API Halsa
    actor T as Trabajador

    AN->>UI: Caso → Portal del paciente → "Generar código"
    UI->>API: POST /casos/{id}/codigos-activacion
    alt El caso no está validado
        API-->>UI: 400 Paciente.CasoNoValidado
        UI-->>AN: "El caso debe estar validado"
    else Validado o posterior
        API-->>UI: 201 { codigo: ABC123-K7Q2M9XD4P, expiraEn }
        Note over API: Solo guarda la sal y el hash.<br/>Un código nuevo invalida el anterior
        UI-->>AN: Muestra el código una sola vez y ofrece copiarlo
        AN-->>T: Se lo entrega fuera del sistema
        Note over T: Lo canjea en el portal (ver 07-paciente.md)
    end
```

## Reglas útiles

- **Asignar:**
  - La asignación es idempotente: asignar dos veces no duplica.
  - El backoffice no tiene todavía un directorio de usuarios. Se asigna por el identificador (`sub`) que cada profesional ve en su página **Cuenta**.
  - El médico laboral y el calificador **no pueden asignarse solos**: depende del analista.
- **Remitir:**
  - No se puede remitir dos veces a la misma IPS mientras la remisión esté vigente.
  - Revocar la remisión le quita a la IPS el acceso al caso.
- **Ver casos:** un analista de ARL_B nunca ve los casos de ARL_A. Si intenta abrir uno, la respuesta es "no encontrado".
