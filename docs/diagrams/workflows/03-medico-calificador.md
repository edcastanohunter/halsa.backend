# 03 · Médico calificador de la ARL (`medico-calificador`)

**Quién es:** el médico que califica la **pérdida de capacidad laboral (PCL)** según el Decreto 1507/2014.

**Permisos:** todo lo del médico laboral ([02](02-medico-laboral-arl.md)), más `pcl:dictamen:read` y `pcl:dictamen:write`.

**Usuario de desarrollo:** `calificador.arla`. Tiene OTP configurado y además el permiso `clinico:emergencia`.

**Lo más sensible del sistema:** el dictamen exige estar asignado, la **verificación reforzada** (OTP), y además que esa verificación tenga **menos de 15 minutos**.

## Mapa de navegación

```mermaid
flowchart LR
    INI["Inicio"]:::pantalla --> CAS["Casos"]:::pantalla --> RES["Resumen"]:::pantalla
    INI --> PLA["Plazos"]:::pantalla
    RES --- EQU["Equipo y remisiones"]:::pantalla
    RES --- PCA["Plazos del caso"]:::pantalla
    RES --- INC["Incapacidades 🔒"]:::clinico
    RES --- HC["Historia clínica 🔒"]:::clinico
    RES --- DIC["Dictamen PCL 🔒<br/>OTP de menos de 15 min"]:::clinico

    classDef pantalla fill:#26a69a2e,stroke:#26a69a,stroke-width:1.5px
    classDef clinico fill:#7e57c233,stroke:#9575cd,stroke-width:1.5px
```

## Flujo de trabajo: del análisis al dictamen en firme

```mermaid
flowchart TD
    D0["El analista lo asigna al caso"]:::sistema --> D1["Revisa la historia clínica 🔒<br/>(conceptos, evoluciones, incapacidades)"]:::clinico
    D1 --> D2["Dictamen PCL"]:::pantalla
    D2 --> D3{"¿OTP de menos de 15 min?"}
    D3 -- no --> D4["Verificar de nuevo<br/>(ventana emergente: no pierde lo escrito)"]:::accion --> D5
    D3 -- sí --> D5{"¿Ya hay un dictamen?"}
    D5 -- no --> D6["Registrar dictamen"]:::accion
    D6 --> D7["% PCL (0–100) · origen (laboral, común o mixto) ·<br/>fecha de estructuración ≤ fecha del dictamen ≤ hoy ·<br/>componentes (deben sumar el %) ·<br/>fundamentos (50–20 000 caracteres) · diagnósticos CIE-10"]:::clinico
    D7 --> D8["Dictamen PENDIENTE.<br/>El caso pasa a EN CALIFICACIÓN PCL.<br/>El analista ve solo el %"]:::sistema

    D5 -- "sí, PENDIENTE" --> D9{"¿Hay que corregirlo?"}
    D9 -- sí --> D10["Corregir: un dictamen nuevo<br/>que reemplaza al vigente (corrigeA)"]:::clinico --> D8
    D9 -- no --> N1["Registrar notificación:<br/>fecha + medio (correo certificado,<br/>electrónico o personal)"]:::accion
    D8 --> N1
    N1 --> N2["NOTIFICADO.<br/>Webhook dictamen.notificado (sin fundamentos).<br/>El trabajador ve el hito en su portal"]:::sistema
    N2 --> N3["Se abre CONTROVERSIA_DICTAMEN<br/>10 días hábiles ⏱"]:::plazo
    N3 --> N4{"¿Vence sin controversia?"}
    N4 -- sí --> N5["EN FIRME (lo hace el Worker)"]:::sistema
    N4 -. "controversia: aún no<br/>se puede registrar" .-> N6["EN CONTROVERSIA"]:::pendiente

    D5 -- "sí, ya notificado" --> X1["Ya no se puede corregir<br/>(409 Dictamen.YaNotificado)"]:::riesgo

    classDef pantalla fill:#26a69a2e,stroke:#26a69a,stroke-width:1.5px
    classDef accion fill:#26a69a0a,stroke:#26a69a,stroke-width:2px
    classDef clinico fill:#7e57c233,stroke:#9575cd,stroke-width:1.5px
    classDef sistema fill:#78909c2e,stroke:#90a4ae,stroke-width:1.5px,stroke-dasharray:4 3
    classDef plazo fill:#ffb30033,stroke:#ffb300,stroke-width:1.5px
    classDef riesgo fill:#ef535033,stroke:#ef5350,stroke-width:1.5px
    classDef pendiente fill:#9e9e9e1a,stroke:#9e9e9e,stroke-width:1.5px,stroke-dasharray:5 5
```

## Estados del dictamen

```mermaid
stateDiagram-v2
    [*] --> PENDIENTE: registrar
    PENDIENTE --> PENDIENTE: corregir (un dictamen nuevo reemplaza al anterior)
    PENDIENTE --> NOTIFICADO: registrar la notificación
    NOTIFICADO --> EN_FIRME: vence CONTROVERSIA_DICTAMEN sin controversia (Worker)
    NOTIFICADO --> EN_CONTROVERSIA: (sin transición implementada)
    EN_FIRME --> [*]
```

## Acceso de emergencia (break-the-glass)

El calificador de desarrollo tiene `clinico:emergencia`. Si necesita leer la historia o el dictamen de un caso **al que no está asignado**:

```mermaid
flowchart LR
    E1["Abre un caso sin estar asignado"]:::pantalla --> E2["403: no asignado"]:::riesgo
    E2 --> E3["'Acceso de emergencia':<br/>escribe el motivo (20–500 caracteres)<br/>y confirma que es una urgencia"]:::accion
    E3 --> E4["Lee el contenido 🔒<br/>(solo lectura, nunca escritura)"]:::clinico
    E4 --> E5["Auditado con el motivo.<br/>Webhook auditoria.acceso-emergencia.<br/>Se abre una revisión de 72 h"]:::sistema
    E5 --> E6["El oficial de privacidad lo revisa<br/>(ver 04-oficial-privacidad.md)"]:::pantalla

    classDef pantalla fill:#26a69a2e,stroke:#26a69a,stroke-width:1.5px
    classDef accion fill:#26a69a0a,stroke:#26a69a,stroke-width:2px
    classDef clinico fill:#7e57c233,stroke:#9575cd,stroke-width:1.5px
    classDef sistema fill:#78909c2e,stroke:#90a4ae,stroke-width:1.5px,stroke-dasharray:4 3
    classDef riesgo fill:#ef535033,stroke:#ef5350,stroke-width:1.5px
```

La emergencia **no salta** el tenant ni la remisión: solo salta la asignación. Registrar o notificar un dictamen siempre exige estar asignado.
