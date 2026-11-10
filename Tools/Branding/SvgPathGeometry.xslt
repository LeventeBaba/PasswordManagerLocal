<?xml version="1.0" encoding="utf-8"?>
<xsl:stylesheet version="1.0"
    xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
    xmlns:svg="http://www.w3.org/2000/svg"
    exclude-result-prefixes="svg">
  <xsl:template match="svg:path" mode="geometry">
    <xsl:value-of select="@d" />
  </xsl:template>

  <xsl:template match="svg:circle" mode="geometry">
    <xsl:value-of select="concat('M', @cx - @r, ' ', @cy,
      'a', @r, ' ', @r, ' 0 1 0 ', 2 * @r, ' 0',
      'a', @r, ' ', @r, ' 0 1 0 ', -2 * @r, ' 0Z')" />
  </xsl:template>

  <xsl:template match="svg:rect" mode="geometry">
    <xsl:variable name="radius" select="@rx" />
    <xsl:variable name="width" select="@width - 2 * $radius" />
    <xsl:variable name="height" select="@height - 2 * $radius" />
    <xsl:variable name="arc" select="concat('a', $radius, ' ', $radius, ' 0 0 1 ')" />
    <xsl:value-of select="concat('M', @x + $radius, ' ', @y,
      'h', $width, $arc, $radius, ' ', $radius,
      'v', $height, $arc, -$radius, ' ', $radius,
      'h', -$width, $arc, -$radius, ' ', -$radius,
      'v', -$height, $arc, $radius, ' ', -$radius, 'Z')" />
  </xsl:template>
</xsl:stylesheet>
